using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace HardLinkerApp;

internal static class Program
{
    private const string DuplicateFolderName = "arquivos_duplicados";

    private static int Main(string[] args)
    {
        AppConfig config;

        try
        {
            config = ResolveConfig(args);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operacao cancelada pelo usuario.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro de configuracao: {ex.Message}");
            return 1;
        }

        try
        {
            var result = Execute(config);

            Console.WriteLine();
            Console.WriteLine("Processamento finalizado.");
            Console.WriteLine($"Arquivos varridos: {result.ScannedFiles}");
            Console.WriteLine($"Grupos duplicados: {result.DuplicateGroups}");
            Console.WriteLine($"Arquivos movidos: {result.MovedFiles}");
            Console.WriteLine($"Hardlinks criados: {result.CreatedHardLinks}");
            Console.WriteLine($"Erros ignorados: {result.IgnoredErrors}");
            Console.WriteLine($"Espaco duplicado total: {result.SavedBytes} bytes ({ToReadableSize(result.SavedBytes)})");
            Console.WriteLine($"Log: {result.LogFilePath}");
            Console.WriteLine($"Manifesto: {result.ManifestPath}");
            Console.WriteLine($"Script restauracao: {result.RestoreScriptPath}");

            return result.Cancelled ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operacao cancelada.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Falha inesperada: {ex}");
            return 1;
        }
    }

    private static ExecutionResult Execute(AppConfig config)
    {
        Directory.CreateDirectory(config.DestinationPath);

        var events = new ConcurrentQueue<OperationEvent>();
        var groups = new ConcurrentDictionary<string, ConcurrentBag<FileEntry>>(StringComparer.OrdinalIgnoreCase);

        long scannedFiles = 0;
        long ignoredErrors = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = config.Threads
        };

        Parallel.ForEach(EnumerateFilesSafe(config.SourcePath, events), parallelOptions, filePath =>
        {
            try
            {
                var entry = BuildFileEntry(filePath, config.HashAlgorithm);
                var key = BuildGroupKey(entry, config.ValidationLevel);

                var bag = groups.GetOrAdd(key, _ => new ConcurrentBag<FileEntry>());
                bag.Add(entry);

                Interlocked.Increment(ref scannedFiles);
            }
            catch (Exception ex)
            {
                events.Enqueue(OperationEvent.Error("HASH", filePath, ex.Message));
                Interlocked.Increment(ref ignoredErrors);
            }
        });

        var duplicateGroups = groups.Values
            .Where(b => b.Count > 1)
            .Select(b => b.ToArray())
            .Where(g => g.Length > 1)
            .ToArray();

        var manifestItems = new List<ManifestItem>(capacity: 1024);

        long movedFiles = 0;
        long createdHardLinks = 0;
        long savedBytes = 0;
        var cancelled = false;

        foreach (var group in duplicateGroups)
        {
            var winner = SelectWinner(group);
            var losers = group
                .Where(f => !string.Equals(f.FullPath, winner.FullPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var loser in losers)
            {
                var actionResult = ProcessLoser(config, winner, loser, events, manifestItems);
                if (actionResult == ActionResult.Cancelled)
                {
                    cancelled = true;
                    break;
                }

                if (actionResult == ActionResult.Success)
                {
                    movedFiles++;
                    createdHardLinks++;
                    savedBytes += loser.Size;
                }
                else
                {
                    ignoredErrors++;
                }
            }

            if (cancelled)
            {
                break;
            }
        }

        var timestamp = DateTimeOffset.Now;
        var logFilePath = Path.Combine(config.DestinationPath, $"hardlinker_{timestamp:yyyyMMdd_HHmmss}.log");
        var manifestPath = Path.Combine(config.DestinationPath, $"manifest_{timestamp:yyyyMMdd_HHmmss}.json");
        var restoreScriptPath = Path.Combine(config.DestinationPath, "restore_originals.ps1");
        var unlinkScriptPath = Path.Combine(config.DestinationPath, "remove_hardlinks_only.ps1");

        WriteLog(logFilePath, events);
        WriteManifest(manifestPath, manifestItems);
        WriteRestoreScript(restoreScriptPath, manifestPath);
        WriteUnlinkScript(unlinkScriptPath, manifestPath);

        return new ExecutionResult(
            ScannedFiles: scannedFiles,
            DuplicateGroups: duplicateGroups.Length,
            MovedFiles: movedFiles,
            CreatedHardLinks: createdHardLinks,
            IgnoredErrors: ignoredErrors,
            SavedBytes: savedBytes,
            Cancelled: cancelled,
            LogFilePath: logFilePath,
            ManifestPath: manifestPath,
            RestoreScriptPath: restoreScriptPath
        );
    }

    private static ActionResult ProcessLoser(
        AppConfig config,
        FileEntry winner,
        FileEntry loser,
        ConcurrentQueue<OperationEvent> events,
        List<ManifestItem> manifestItems)
    {
        var loserRelativePath = GetRelativeInsideRoot(config.SourcePath, loser.FullPath);
        var backupRelativePath = loserRelativePath.Replace('\\', '/');
        var backupPath = BuildUniqueDestinationPath(Path.Combine(config.DestinationPath, loserRelativePath));

        if (!TryMoveWithUserDecision(loser.FullPath, backupPath, events, out var cancelled))
        {
            return cancelled ? ActionResult.Cancelled : ActionResult.Ignored;
        }

        var linkOutcome = TryCreateHardLinkWithUserDecision(loser.FullPath, winner.FullPath, backupPath, events, out cancelled);
        if (linkOutcome == ActionResult.Success)
        {
            manifestItems.Add(new ManifestItem(
                WinnerPath: winner.FullPath,
                LoserOriginalPath: loser.FullPath,
                MovedBackupPath: backupPath,
                BackupRelativePath: backupRelativePath,
                Size: loser.Size,
                Timestamp: DateTimeOffset.Now));
        }

        return cancelled ? ActionResult.Cancelled : linkOutcome;
    }

    private static bool TryMoveWithUserDecision(
        string sourcePath,
        string destinationPath,
        ConcurrentQueue<OperationEvent> events,
        out bool cancelled)
    {
        cancelled = false;

        while (true)
        {
            try
            {
                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Move(sourcePath, destinationPath);
                events.Enqueue(OperationEvent.Move(sourcePath, destinationPath));
                return true;
            }
            catch (Exception ex)
            {
                events.Enqueue(OperationEvent.Error("MOVE", sourcePath, ex.Message));

                var choice = AskUserForErrorDecision(
                    $"Erro ao mover arquivo: {sourcePath}\nDestino: {destinationPath}\nDetalhe: {ex.Message}");

                if (choice == ErrorDecision.Retry)
                {
                    continue;
                }

                if (choice == ErrorDecision.Ignore)
                {
                    events.Enqueue(OperationEvent.Info("IGNORE", $"Move ignorado para {sourcePath}"));
                    return false;
                }

                cancelled = true;
                events.Enqueue(OperationEvent.Info("CANCEL", "Cancelado pelo usuario durante MOVE."));
                return false;
            }
        }
    }

    private static ActionResult TryCreateHardLinkWithUserDecision(
        string linkPath,
        string targetPath,
        string backupPath,
        ConcurrentQueue<OperationEvent> events,
        out bool cancelled)
    {
        cancelled = false;

        while (true)
        {
            try
            {
                CreateHardLinkOrThrow(linkPath, targetPath);
                events.Enqueue(OperationEvent.Link(linkPath, targetPath));
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                events.Enqueue(OperationEvent.Error("LINK", linkPath, ex.Message));

                var choice = AskUserForErrorDecision(
                    $"Erro ao criar hardlink: {linkPath}\nTarget: {targetPath}\nDetalhe: {ex.Message}");

                if (choice == ErrorDecision.Retry)
                {
                    continue;
                }

                var rollbackOk = TryRollbackMove(backupPath, linkPath, events);
                if (!rollbackOk)
                {
                    events.Enqueue(OperationEvent.Error("ROLLBACK", linkPath, "Falha ao restaurar arquivo apos erro de hardlink."));
                }

                if (choice == ErrorDecision.Ignore)
                {
                    events.Enqueue(OperationEvent.Info("IGNORE", $"Link ignorado para {linkPath}"));
                    return ActionResult.Ignored;
                }

                cancelled = true;
                events.Enqueue(OperationEvent.Info("CANCEL", "Cancelado pelo usuario durante LINK."));
                return ActionResult.Cancelled;
            }
        }
    }

    private static bool TryRollbackMove(string movedBackupPath, string originalPath, ConcurrentQueue<OperationEvent> events)
    {
        try
        {
            if (!File.Exists(movedBackupPath))
            {
                return false;
            }

            var originalDirectory = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrWhiteSpace(originalDirectory))
            {
                Directory.CreateDirectory(originalDirectory);
            }

            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }

            File.Move(movedBackupPath, originalPath);
            events.Enqueue(OperationEvent.Info("ROLLBACK", $"Arquivo restaurado em {originalPath}"));
            return true;
        }
        catch (Exception ex)
        {
            events.Enqueue(OperationEvent.Error("ROLLBACK", originalPath, ex.Message));
            return false;
        }
    }

    private static FileEntry BuildFileEntry(string path, HashAlgorithmType hashAlgorithm)
    {
        var info = new FileInfo(path);
        var depth = CalculatePathDepth(path);
        var hash = ComputeFileHash(path, hashAlgorithm);
        return new FileEntry(path, info.Name, info.Length, hash, depth);
    }

    private static string ComputeFileHash(string path, HashAlgorithmType hashAlgorithm)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.SequentialScan);

        var hash = hashAlgorithm switch
        {
            HashAlgorithmType.Md5 => MD5.HashData(stream),
            HashAlgorithmType.Sha1 => SHA1.HashData(stream),
            _ => throw new ArgumentOutOfRangeException(nameof(hashAlgorithm), hashAlgorithm, "Algoritmo nao suportado.")
        };

        return Convert.ToHexString(hash);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, ConcurrentQueue<OperationEvent> events)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception ex)
            {
                events.Enqueue(OperationEvent.Error("ENUM_FILES", current, ex.Message));
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex)
            {
                events.Enqueue(OperationEvent.Error("ENUM_DIRS", current, ex.Message));
                continue;
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private static string BuildGroupKey(FileEntry entry, ValidationLevel level)
    {
        return level switch
        {
            ValidationLevel.HashSizeName => $"{entry.Hash}|{entry.Size}|{entry.Name}",
            ValidationLevel.HashSize => $"{entry.Hash}|{entry.Size}",
            ValidationLevel.NameSize => $"{entry.Name}|{entry.Size}",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Nivel de validacao invalido.")
        };
    }

    private static FileEntry SelectWinner(IReadOnlyCollection<FileEntry> group)
    {
        return group
            .OrderBy(f => f.Depth)
            .ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static int CalculatePathDepth(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var relative = Path.GetRelativePath(root, directory);

        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return 0;
        }

        return relative
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static string BuildUniqueDestinationPath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        var index = 1;
        while (true)
        {
            var candidate = Path.Combine(directory, $"{fileName}.dup{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static string GetRelativeInsideRoot(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Arquivo fora da raiz de origem: {filePath}");
        }

        return relative;
    }

    private static ErrorDecision AskUserForErrorDecision(string message)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine(message);
            Console.Write("Escolha [R] Tentar novamente, [I] Ignorar, [C] Cancelar tudo: ");
            var input = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (input == "R")
            {
                return ErrorDecision.Retry;
            }

            if (input == "I")
            {
                return ErrorDecision.Ignore;
            }

            if (input == "C")
            {
                return ErrorDecision.Cancel;
            }
        }
    }

    private static AppConfig ResolveConfig(string[] args)
    {
        if (TryParseArgs(args, out var parsed))
        {
            return ValidateConfig(parsed);
        }

        return ValidateConfig(ReadInteractiveConfig());
    }

    private static bool TryParseArgs(string[] args, out AppConfig config)
    {
        config = default!;

        if (args.Length == 0)
        {
            return false;
        }

        if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            throw new OperationCanceledException("Ajuda solicitada.");
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[i + 1];
                i++;
            }
            else
            {
                map[key] = "true";
            }
        }

        if (!map.TryGetValue("source", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var dest = map.TryGetValue("dest", out var parsedDest) && !string.IsNullOrWhiteSpace(parsedDest)
            ? parsedDest
            : Environment.CurrentDirectory;

        var threads = DefaultThreads();
        if (map.TryGetValue("threads", out var threadsRaw) && int.TryParse(threadsRaw, out var parsedThreads))
        {
            threads = parsedThreads;
        }

        if (!map.TryGetValue("hash", out var hashRaw) || !TryParseHash(hashRaw, out var hash))
        {
            return false;
        }

        if (!map.TryGetValue("validation", out var validationRaw) || !TryParseValidation(validationRaw, out var validation))
        {
            return false;
        }

        config = new AppConfig(source.Trim(), dest.Trim(), threads, hash, validation);
        return true;
    }

    private static AppConfig ReadInteractiveConfig()
    {
        Console.WriteLine("HardLinker - Configuracao inicial");
        Console.WriteLine($"Pasta atual: {Environment.CurrentDirectory}");
        Console.WriteLine("A origem pode ser informada apenas pelo nome da pasta (relativo a pasta atual).");
        Console.WriteLine();

        var source = AskExistingDirectory("Pasta de busca (origem)");
        var destinationBaseDefault = Environment.CurrentDirectory;
        var destinationBase = AskExistingDirectoryOptional(
            $"Pasta de destino base [Enter = {destinationBaseDefault}]",
            destinationBaseDefault);
        var destinationPreview = EnsureDuplicateSubfolder(Path.GetFullPath(destinationBase));
        Console.WriteLine($"Arquivos duplicados serao movidos para a subpasta: {destinationPreview}");

        var defaultThreads = DefaultThreads();
        var threads = AskInt($"Quantidade de threads [padrao: {defaultThreads}]", defaultThreads, min: 1);

        var hash = AskHashAlgorithmByNumber(HashAlgorithmType.Md5);
        var validation = AskValidationLevelByNumber(ValidationLevel.HashSizeName);

        return new AppConfig(source, destinationBase, threads, hash, validation);
    }

    private static AppConfig ValidateConfig(AppConfig config)
    {
        var source = Path.GetFullPath(config.SourcePath);
        var destinationBase = Path.GetFullPath(config.DestinationPath);
        var destination = EnsureDuplicateSubfolder(destinationBase);
        var threads = Math.Max(1, config.Threads);

        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException($"Pasta de origem nao existe: {source}");
        }

        if (!Directory.Exists(destinationBase))
        {
            throw new InvalidOperationException($"Pasta base de destino nao existe: {destinationBase}");
        }

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Origem e destino nao podem ser iguais.");
        }

        if (IsSubPath(source, destination))
        {
            throw new InvalidOperationException("Destino nao pode estar dentro da origem.");
        }

        return config with
        {
            SourcePath = source,
            DestinationPath = destination,
            Threads = threads
        };
    }

    private static bool IsSubPath(string parentPath, string childPath)
    {
        var parent = EnsureTrailingSeparator(Path.GetFullPath(parentPath));
        var child = EnsureTrailingSeparator(Path.GetFullPath(childPath));
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string AskRequiredText(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(input))
            {
                return input;
            }
        }
    }

    private static string AskOptionalText(string label, string defaultValue)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            return input;
        }
    }

    private static string AskExistingDirectory(string label)
    {
        while (true)
        {
            var input = AskRequiredText(label);
            var fullPath = Path.GetFullPath(input);

            if (Directory.Exists(fullPath))
            {
                return input;
            }

            Console.WriteLine($"Pasta nao encontrada: {fullPath}");
        }
    }

    private static string AskExistingDirectoryOptional(string label, string defaultValue)
    {
        while (true)
        {
            var input = AskOptionalText(label, defaultValue);
            var fullPath = Path.GetFullPath(input);

            if (Directory.Exists(fullPath))
            {
                return input;
            }

            Console.WriteLine($"Pasta nao encontrada: {fullPath}");
        }
    }

    private static int AskInt(string label, int defaultValue, int min)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            if (int.TryParse(input, out var value) && value >= min)
            {
                return value;
            }
        }
    }

    private static HashAlgorithmType AskHashAlgorithmByNumber(HashAlgorithmType defaultValue)
    {
        var defaultNumber = defaultValue == HashAlgorithmType.Md5 ? 1 : 2;

        while (true)
        {
            Console.WriteLine("Tipo de hash:");
            Console.WriteLine("  1 - MD5 (padrao)");
            Console.WriteLine("  2 - SHA1");
            Console.Write($"Opcao de hash [padrao: {defaultNumber}]: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            if (TryParseHash(input, out var value))
            {
                return value;
            }

            Console.WriteLine("Opcao invalida. Informe 1 (MD5) ou 2 (SHA1).");
        }
    }

    private static ValidationLevel AskValidationLevelByNumber(ValidationLevel defaultValue)
    {
        var defaultNumber = (int)defaultValue;

        while (true)
        {
            Console.WriteLine("Nivel de validacao de duplicidade:");
            Console.WriteLine("  1 - hash + tamanho + nome (mais restrito)");
            Console.WriteLine("  2 - hash + tamanho");
            Console.WriteLine("  3 - nome + tamanho (mais rapido, menos confiavel)");
            Console.Write($"Opcao de validacao [padrao: {defaultNumber}]: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            if (TryParseValidation(input, out var value))
            {
                return value;
            }

            Console.WriteLine("Opcao invalida. Informe 1, 2 ou 3.");
        }
    }

    private static bool TryParseHash(string raw, out HashAlgorithmType hash)
    {
        hash = default;
        if (raw == "1")
        {
            hash = HashAlgorithmType.Md5;
            return true;
        }

        if (raw == "2")
        {
            hash = HashAlgorithmType.Sha1;
            return true;
        }

        if (string.Equals(raw, "md5", StringComparison.OrdinalIgnoreCase))
        {
            hash = HashAlgorithmType.Md5;
            return true;
        }

        if (string.Equals(raw, "sha1", StringComparison.OrdinalIgnoreCase))
        {
            hash = HashAlgorithmType.Sha1;
            return true;
        }

        return false;
    }

    private static bool TryParseValidation(string raw, out ValidationLevel validation)
    {
        validation = default;

        if (!int.TryParse(raw, out var number))
        {
            return false;
        }

        if (number is < 1 or > 3)
        {
            return false;
        }

        validation = (ValidationLevel)number;
        return true;
    }

    private static int DefaultThreads() => Math.Max(1, Environment.ProcessorCount / 2);

    private static string EnsureDuplicateSubfolder(string destinationBasePath)
    {
        var normalizedBase = Path.GetFullPath(destinationBasePath);
        var folderName = Path.GetFileName(normalizedBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(folderName, DuplicateFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedBase;
        }

        return Path.Combine(normalizedBase, DuplicateFolderName);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Uso:");
        Console.WriteLine("  HardLinkerApp --source <pasta> [--dest <pasta_base>] [--threads <n>] [--hash <1|2>] [--validation <1|2|3>]");
        Console.WriteLine("Hash: 1=MD5 (padrao), 2=SHA1.");
        Console.WriteLine("Validacao: 1=hash+tamanho+nome, 2=hash+tamanho, 3=nome+tamanho.");
        Console.WriteLine($"Destino final sempre sera uma subpasta '{DuplicateFolderName}' dentro da pasta base de destino.");
        Console.WriteLine("Se argumentos obrigatorios nao forem informados, o modo interativo sera usado.");
    }

    private static void WriteLog(string logPath, ConcurrentQueue<OperationEvent> events)
    {
        var ordered = events.OrderBy(e => e.Timestamp).ToArray();
        var lines = ordered.Select(e => $"{e.Timestamp:O} | {e.EventType} | {e.Message}");
        File.WriteAllLines(logPath, lines);
    }

    private static void WriteManifest(string manifestPath, List<ManifestItem> manifestItems)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(manifestItems, jsonOptions);
        File.WriteAllText(manifestPath, json);
    }

    private static void WriteRestoreScript(string scriptPath, string manifestPath)
    {
        var manifestFileName = Path.GetFileName(manifestPath);
        var script = $@"param(
    [string]$ManifestPath = '{manifestFileName}',
    [string]$MergedDestinationRoot = ''
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {{
    $ManifestPath = Join-Path $scriptDir $ManifestPath
}}

if (-not (Test-Path -LiteralPath $ManifestPath)) {{
    Write-Error ('Manifesto nao encontrado: ' + $ManifestPath)
    exit 1
}}

$items = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
foreach ($item in $items) {{
    $backupPath = $item.MovedBackupPath

    if (-not (Test-Path -LiteralPath $backupPath) -and -not [string]::IsNullOrWhiteSpace($MergedDestinationRoot)) {{
        $relative = $item.BackupRelativePath -replace '/', '\\'
        $candidate = Join-Path $MergedDestinationRoot $relative
        if (Test-Path -LiteralPath $candidate) {{
            $backupPath = $candidate
        }}
    }}

    if (-not (Test-Path -LiteralPath $backupPath)) {{
        Write-Warning ('Backup nao encontrado para restauracao: ' + $item.LoserOriginalPath)
        continue
    }}

    if (Test-Path -LiteralPath $item.LoserOriginalPath) {{
        Remove-Item -LiteralPath $item.LoserOriginalPath -Force
    }}

    $targetDir = Split-Path -Parent $item.LoserOriginalPath
    if (-not (Test-Path -LiteralPath $targetDir)) {{
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }}

    Move-Item -LiteralPath $backupPath -Destination $item.LoserOriginalPath -Force

    if (Test-Path -LiteralPath $item.LoserOriginalPath) {{
        Write-Host ('RESTORED: ' + $item.LoserOriginalPath)
    }}
}}

Write-Host 'Restauracao concluida.'
";

        File.WriteAllText(scriptPath, script);
    }

    private static void WriteUnlinkScript(string scriptPath, string manifestPath)
    {
        var manifestFileName = Path.GetFileName(manifestPath);
        var script = $@"param(
    [string]$ManifestPath = '{manifestFileName}'
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {{
    $ManifestPath = Join-Path $scriptDir $ManifestPath
}}

if (-not (Test-Path -LiteralPath $ManifestPath)) {{
    Write-Error ('Manifesto nao encontrado: ' + $ManifestPath)
    exit 1
}}

$items = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
foreach ($item in $items) {{
    if (Test-Path -LiteralPath $item.LoserOriginalPath) {{
        Remove-Item -LiteralPath $item.LoserOriginalPath -Force
        Write-Host ('REMOVED LINK: ' + $item.LoserOriginalPath)
    }}
}}

Write-Host 'Remocao de hardlinks concluida.'
";

        File.WriteAllText(scriptPath, script);
    }

    private static string ToReadableSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private static void CreateHardLinkOrThrow(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Criacao de hardlink suportada apenas em Windows nesta implementacao.");
        }

        var created = CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero);
        if (!created)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Falha ao criar hardlink. link={linkPath}, target={targetPath}");
        }
    }

    [DllImport("Kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}

internal enum HashAlgorithmType
{
    Md5,
    Sha1
}

internal enum ValidationLevel
{
    HashSizeName = 1,
    HashSize = 2,
    NameSize = 3
}

internal enum ErrorDecision
{
    Retry,
    Ignore,
    Cancel
}

internal enum ActionResult
{
    Success,
    Ignored,
    Cancelled
}

internal readonly record struct AppConfig(
    string SourcePath,
    string DestinationPath,
    int Threads,
    HashAlgorithmType HashAlgorithm,
    ValidationLevel ValidationLevel);

internal readonly record struct FileEntry(
    string FullPath,
    string Name,
    long Size,
    string Hash,
    int Depth);

internal readonly record struct OperationEvent(DateTimeOffset Timestamp, string EventType, string Message)
{
    public static OperationEvent Move(string source, string destination) =>
        new(DateTimeOffset.Now, "MOVE", $"origem={source} | destino={destination}");

    public static OperationEvent Link(string linkPath, string targetPath) =>
        new(DateTimeOffset.Now, "LINK", $"link={linkPath} | target={targetPath}");

    public static OperationEvent Error(string action, string path, string error) =>
        new(DateTimeOffset.Now, "ERROR", $"acao={action} | arquivo={path} | erro={error}");

    public static OperationEvent Info(string action, string message) =>
        new(DateTimeOffset.Now, action, message);
}

internal readonly record struct ManifestItem(
    string WinnerPath,
    string LoserOriginalPath,
    string MovedBackupPath,
    string BackupRelativePath,
    long Size,
    DateTimeOffset Timestamp);

internal readonly record struct ExecutionResult(
    long ScannedFiles,
    int DuplicateGroups,
    long MovedFiles,
    long CreatedHardLinks,
    long IgnoredErrors,
    long SavedBytes,
    bool Cancelled,
    string LogFilePath,
    string ManifestPath,
    string RestoreScriptPath);
