# SPEC DE IMPLEMENTACAO - HARDLINKER APP

## 1. Objetivo

Definir o comportamento obrigatorio da aplicacao de console HardLinker para que agentes de IA (Copilot, Codex e outros) implementem de forma consistente, performatica e segura.

## 2. Escopo Funcional

A aplicacao deve:

1. Receber uma pasta de busca (origem).
2. Receber uma pasta de destino (quarentena de duplicados).
3. Receber quantidade de threads para processamento.
4. Receber tipo de hash: md5 ou sha1.
5. Receber nivel de validacao de duplicidade:
   - Nivel 1: hash + tamanho + nome
   - Nivel 2: hash + tamanho
   - Nivel 3: nome + tamanho
6. Percorrer a pasta de busca e todas as subpastas.
7. Calcular hash e metadados de cada arquivo (caminho, tamanho, nome).
8. Detectar duplicados conforme o nivel de validacao.
9. Manter o arquivo "vencedor" mais proximo da raiz da unidade.
10. Mover os demais duplicados para a pasta de destino preservando estrutura original.
11. Criar hardlink no caminho original do duplicado apontando para o arquivo vencedor.
12. Gerar relatorio em log na pasta de destino com data/hora de cada evento.
13. Exibir no console total de arquivos processados, total de duplicados tratados e espaco duplicado total.
14. Em caso de erro ao mover arquivo, solicitar decisao do usuario: tentar novamente, ignorar, cancelar tudo.
15. Salvar arquivo com comandos para remover hardlinks criados e restaurar arquivos para local original (sem hardlinks).

## 3. Modelo de Entrada

A aplicacao pode operar em dois modos:

1. Modo argumentos de linha de comando.
2. Menu interativo inicial.

Se faltar argumento obrigatorio, cair para modo interativo para completar os dados.

### 3.1 Argumentos sugeridos

- --source <caminho>
- --dest <caminho>
- --threads <int>
- --hash <md5|sha1>
- --validation <1|2|3>

### 3.2 Regras de default e validacao

- threads default: max(1, Environment.ProcessorCount / 2)
- source obrigatorio
- dest obrigatorio e diferente de source
- hash obrigatorio: md5 ou sha1
- validation obrigatorio: 1, 2 ou 3
- criar pasta de destino se nao existir
- impedir destino dentro da origem e origem dentro do destino (evitar recursao/colisao)

## 4. Regras de Duplicidade e Escolha do Arquivo Vencedor

Para cada grupo de arquivos considerados iguais conforme nivel de validacao:

1. Vence o arquivo com menor profundidade de pasta em relacao a raiz da unidade.
2. Profundidade = quantidade de segmentos de diretorio apos "X:\".
3. Em empate de profundidade, vence o menor caminho em ordem alfabetica (ordinal ignore case) para manter determinismo.

Todos os outros arquivos do grupo sao duplicados perdedores.

## 5. Processamento e Estruturas (Foco em Performance)

Implementar com paradigma funcional e processamento concorrente.

### 5.1 Estruturas recomendadas

- record FileEntry(string FullPath, string Name, long Size, string Hash, int Depth)
- ConcurrentDictionary<string, ConcurrentBag<FileEntry>> para agrupamento por chave de comparacao
- ConcurrentQueue<OperationEvent> para eventos de log
- Colecao imutavel para configuracao (record AppConfig)

### 5.2 Pipeline recomendado

1. Enumerar arquivos com streaming (Directory.EnumerateFiles recursivo).
2. Projetar para metadados basicos (nome, tamanho, caminho, profundidade).
3. Calcular hash em paralelo com limite de threads configurado.
4. Montar chave de validacao conforme nivel:
   - Nivel 1: "{Hash}|{Size}|{Name}"
   - Nivel 2: "{Hash}|{Size}"
   - Nivel 3: "{Name}|{Size}"
5. Agrupar em ConcurrentDictionary.
6. Filtrar grupos com contagem > 1.
7. Para cada grupo, escolher vencedor e processar perdedores.

### 5.3 Observacoes de desempenho

- Usar FileStream com buffer adequado para hash.
- Evitar carregar arquivo inteiro em memoria.
- Evitar lock global; usar colecoes concorrentes.
- Separar etapa CPU/IO quando possivel (producer-consumer).
- Registrar contadores com Interlocked.

## 6. Regras de Movimentacao e Hardlink

Para cada arquivo perdedor:

1. Calcular caminho espelho no destino:
   - destino + caminho relativo a pasta de busca
2. Criar diretorios necessarios no destino.
3. Mover arquivo perdedor para caminho espelho.
4. Criar hardlink no caminho original perdedor apontando para o arquivo vencedor.

Resultado esperado:

- Caminho original do perdedor continua existindo, agora como hardlink para o vencedor.
- Conteudo fica deduplicado fisicamente.
- Arquivo original movido fica guardado no destino para auditoria/rollback.

## 7. Tratamento de Erros Interativos

Em falha ao mover arquivo:

1. Mostrar erro com contexto (arquivo origem, destino, excecao).
2. Perguntar opcao:
   - R = tentar novamente
   - I = ignorar este arquivo
   - C = cancelar toda operacao
3. Repetir tentativa enquanto usuario escolher R.
4. Em I, registrar evento e seguir.
5. Em C, interromper processamento com encerramento controlado e gerar relatorio parcial.

Observacao: em modo multithread, centralizar prompts interativos em fluxo sincronizado para evitar concorrencia no console.

## 8. Logs e Relatorio Final

Gerar arquivo .log na pasta de destino.

Nome sugerido:

- hardlinker_yyyyMMdd_HHmmss.log

Cada evento deve ter timestamp local no formato ISO:

- 2026-07-14T10:33:21.123-03:00 | MOVE | origem=... | destino=...
- 2026-07-14T10:33:21.456-03:00 | LINK | link=... | target=...
- 2026-07-14T10:33:22.010-03:00 | ERROR | acao=MOVE | arquivo=... | erro=...

Ao finalizar, exibir no console:

1. Total de arquivos varridos.
2. Total de grupos duplicados.
3. Total de arquivos movidos.
4. Total de hardlinks criados.
5. Total de erros ignorados.
6. Espaco duplicado total economizado em bytes e em unidade legivel.

## 9. Arquivo de Comandos de Reversao

Salvar na pasta de destino um arquivo de comandos para remover hardlinks e restaurar arquivos originais.

Nome sugerido:

- restore_originals.ps1

Esse script deve usar um manifesto gerado pela aplicacao contendo, por item:

- WinnerPath
- LoserOriginalPath
- MovedBackupPath

Fluxo do script de restauracao (obrigatorio):

1. Remover o hardlink em LoserOriginalPath.
2. Garantir diretorio de LoserOriginalPath.
3. Mover MovedBackupPath de volta para LoserOriginalPath.
4. Validar existencia final do arquivo restaurado.

Objetivo de restauracao:

- Se o usuario mesclar a pasta de destino na origem, deve ser possivel restaurar todos os arquivos para seus locais originais, sem hardlinks.

## 10. Paradigma Funcional Obrigatorio

Diretrizes de codigo:

1. Priorizar funcoes puras para transformacoes (path -> relative path, metadata -> key, grupo -> vencedor/perdedores).
2. Isolar efeitos colaterais (IO de disco, console, log).
3. Usar records para modelos imutaveis.
4. Evitar estado global mutavel.
5. Funcoes pequenas, composicao e pipeline claro.

## 11. Contratos de Saida Esperados

Arquivos gerados na pasta de destino:

1. Log principal .log
2. Manifesto de operacoes (jsonl ou json)
3. Script de restauracao restore_originals.ps1

Opcional:

4. Script auxiliar remove_hardlinks_only.ps1

## 12. Criterios de Aceitacao

1. Processa pasta e subpastas com concorrencia configuravel.
2. Aplica corretamente os 3 niveis de validacao.
3. Mantem sempre o arquivo mais proximo da raiz como vencedor.
4. Move perdedores para destino mantendo estrutura.
5. Recria no caminho original hardlink para vencedor.
6. Trata erro de move com retry/ignore/cancel.
7. Gera log temporal detalhado.
8. Exibe resumo final no console com total e espaco duplicado.
9. Gera comandos de restauracao e remove hardlinks na volta.
10. Implementacao com estilo funcional e foco em desempenho.

## 13. Fora de Escopo

1. Interface grafica.
2. Suporte a links simbolicos.
3. Persistencia em banco de dados.
4. Operacoes em rede distribuida.

## 14. Observacoes de Plataforma

- Projeto alvo: executavel de console .NET.
- Sistema principal esperado: Windows (hardlink via File.CreateHardLink ou P/Invoke quando necessario).
- Garantir mensagens claras para erros de permissao e arquivos bloqueados.
