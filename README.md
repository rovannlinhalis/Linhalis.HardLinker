# Linhalis.HardLinker

Aplicação de console em .NET para identificar arquivos duplicados e deduplicar armazenamento no Windows usando hardlinks.

## Problema

Em estruturas grandes de pastas, é comum existir o mesmo arquivo repetido em vários locais. Isso gera:

- Desperdício de espaço em disco
- Dificuldade de organização
- Risco de inconsistências quando cópias diferentes são alteradas

## Solução

O HardLinker percorre uma pasta de origem (com subpastas), detecta duplicados com regras configuráveis e:

1. Mantém apenas um arquivo vencedor por grupo de duplicados
2. Move os demais para uma pasta de backup/quarentena
3. Recria os caminhos originais como hardlinks para o arquivo vencedor

Resultado: os caminhos continuam existindo para os sistemas/usuários, mas o conteúdo físico fica deduplicado.

## Como a aplicação resolve

### 1. Varredura concorrente

A aplicação usa processamento em paralelo para ler metadados e hash dos arquivos com foco em desempenho.

- Threads configuráveis
- Padrão: metade dos núcleos disponíveis
- Estruturas concorrentes para agrupamento

### 2. Critérios de duplicidade

O usuário escolhe o nível de validação:

- 1: hash + tamanho + nome (mais restritivo)
- 2: hash + tamanho
- 3: nome + tamanho (mais rápido, menos confiável)

### 3. Escolha do arquivo vencedor

Dentro de cada grupo duplicado, o arquivo mantido é aquele mais próximo da raiz da unidade (menor profundidade). Em empate, desempate alfabético para manter determinismo.

### 4. Movimentação e hardlink

Para cada arquivo duplicado perdedor:

- Move para a pasta de destino (preservando estrutura relativa)
- Cria hardlink no caminho original apontando para o vencedor

### 5. Rastreabilidade e rollback

Ao final da execução, a aplicação gera na pasta de destino:

- Log detalhado de eventos com timestamp
- Manifesto JSON com as operações realizadas
- Script de restauração (`restore_originals.ps1`)
- Script opcional de remoção de hardlinks (`remove_hardlinks_only.ps1`)

## Exemplo antes e depois

### Antes

```text
C:\Dados\Origem
|-- ProjetoA\arquivo.txt
|-- ProjetoB\arquivo.txt
|-- ProjetoC\Sub\arquivo.txt
```

Supondo que os 3 arquivos sejam iguais pelos critérios escolhidos, o app escolhe um vencedor (o mais próximo da raiz da unidade) e trata os demais como duplicados.

### Depois

```text
C:\Dados\Origem
|-- ProjetoA\arquivo.txt          (vencedor)
|-- ProjetoB\arquivo.txt          (hardlink para ProjetoA\arquivo.txt)
|-- ProjetoC\Sub\arquivo.txt     (hardlink para ProjetoA\arquivo.txt)

C:\Dados\arquivos_duplicados
|-- ProjetoB\arquivo.txt          (backup movido)
|-- ProjetoC\Sub\arquivo.txt     (backup movido)
|-- hardlinker_YYYYMMDD_HHMMSS.log
|-- manifest_YYYYMMDD_HHMMSS.json
|-- restore_originals.ps1
|-- remove_hardlinks_only.ps1
```

Com isso, os caminhos originais continuam atendendo quem consome os arquivos, mas o armazenamento físico fica deduplicado.

## Fluxo de entrada

A aplicação aceita:

- Argumentos de linha de comando
- Menu interativo

No modo interativo:

- Hash é escolhido por número: `1 = MD5 (padrão)`, `2 = SHA1`
- Nível de validação é explicado antes da escolha (`1`, `2`, `3`)
- Pastas informadas são validadas; só avança se existirem
- A pasta atual pode ser usada como destino base (padrão)
- O destino final sempre usa uma subpasta `arquivos_duplicados`

## Requisitos

- Windows (criação de hardlink via API nativa)
- .NET SDK com suporte ao target do projeto (`net10.0`)

## Como executar

### Build

```powershell
dotnet build Linhalis.HardLinker.slnx
```

### Executar com argumentos

```powershell
dotnet run --project HardLinkerApp -- \
  --source "C:\Dados\Origem" \
  --dest "C:\Dados" \
  --threads 8 \
  --hash 1 \
  --validation 1
```

Observações:

- `--hash`: `1=MD5`, `2=SHA1` (também aceita `md5` e `sha1`)
- `--dest` é a pasta base; o app cria e usa `arquivos_duplicados` dentro dela
- Se `--dest` não for informado, a pasta atual é usada como base

### Executar modo interativo

```powershell
dotnet run --project HardLinkerApp
```

## Restauração

Para restaurar arquivos originais sem hardlinks, execute o script gerado na pasta de destino:

```powershell
./restore_originals.ps1
```

Se a pasta de destino tiver sido mesclada em outro local, o script aceita caminho alternativo via parâmetro.

## Status atual

Projeto em evolução, com foco em:

- Performance em grandes volumes de arquivos
- Segurança operacional (log, manifesto e rollback)
- Experiência de uso em linha de comando
