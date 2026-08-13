# AnInfo.MailServer

Servidor SMTP local e reutilizável em C#/.NET 8. Recebe SMTP, preserva o MIME, persiste no PostgreSQL, processa uma fila com retry e usa uma entrega segura de desenvolvimento que apenas registra o envio. Não há entrega externa ou relay público nesta etapa.

## Arquitetura

```text
Cliente SMTP -> SmtpServer -> MimeKit -> PostgreSQL (Pending)
                                      -> DeliveryWorker -> DevelopmentMailDeliveryService -> Sent
GET /health -> aplicação + DbContext PostgreSQL
```

Projetos:

- `Domain`: entidades, destinatários e estados.
- `Application`: contratos e configurações.
- `Infrastructure`: EF Core/Npgsql, SMTP, fila, retry e entrega.
- `Host`: Generic Host, health check e Serilog.
- `TestClient`: envio SMTP e inspeção PostgreSQL.
- `Tests`: integração SMTP com banco PostgreSQL isolado.

O listener continua em SmtpServer 11.1.0 e o MIME é preservado por MimeKit. O worker faz claim transacional com `SELECT ... FOR UPDATE SKIP LOCKED`: workers concorrentes ignoram linhas já bloqueadas e não processam a mesma mensagem simultaneamente.

## Modos de entrega

`Delivery:Mode` seleciona uma única implementação por DI:

- `Development` (padrão): não acessa a Internet; registra o que faria e conclui a tentativa para testes locais.
- `SmtpRelay`: usa MailKit, autenticação e TLS para entregar a um relay configurado. A mensagem só vira `Sent` depois que `SendAsync` recebe a confirmação SMTP do relay.

Configuração nativa .NET em `appsettings.json`:

```json
{
  "Delivery": { "Mode": "Development" },
  "SmtpRelay": {
    "Host": "",
    "Port": 587,
    "Username": "",
    "Password": "",
    "UseStartTls": true,
    "UseSsl": false,
    "FromAddress": "",
    "FromName": "AnInfo Mail Server",
    "PreserveOriginalFrom": false,
    "TimeoutSeconds": 30
  }
}
```

Para porta 587 use `UseStartTls=true`. Para um relay com TLS implícito na porta 465, use `UseSsl=true` e `UseStartTls=false`. A validação normal de certificados do MailKit permanece ativa; certificados inválidos não são aceitos.

Com `PreserveOriginalFrom=false` (recomendado), o header e envelope sender usam `FromAddress`, que deve ser autorizado pelo relay; o primeiro `From` original é colocado em `Reply-To` quando ainda não existe. Com `true`, header e envelope originais são preservados, mas isso só deve ser habilitado quando a política do relay permitir para evitar spoofing/rejeição.

## DeliveryAttempts, estados e retry

Cada execução cria uma linha em `DeliveryAttempts` com número, início/fim, sucesso, status/resposta SMTP e tipo/mensagem de erro limitados. Credenciais e MIME/corpo não são armazenados nessa tabela.

- `Pending`: aguardando primeira tentativa ou retry.
- `Processing`: obtida transacionalmente por um worker.
- `Sent`: o serviço selecionado confirmou sucesso; no modo relay significa que o relay aceitou por SMTP.
- `Failed`: falha permanente ou limite de tentativas atingido.

SMTP 4xx é temporário e segue `Queue:RetryDelaysSeconds` até `MaxRetries`. SMTP 5xx e autenticação inválida são permanentes e vão diretamente para `Failed`. Timeout, DNS, conexão e falhas transitórias de protocolo permitem retry. “Relay accepted” não prova entrega na caixa de entrada: o provedor ainda pode filtrar, atrasar ou gerar bounce.

## Pré-requisitos

- .NET SDK 8.
- Docker Desktop com Docker Compose.
- PowerShell.

## Configurar o ambiente

```powershell
cd C:\caminho\para\AnInfo.MailServer
Copy-Item .env.example .env
```

Edite `.env` e defina uma senha forte em `POSTGRES_PASSWORD`. `.env` está ignorado pelo Git. A senha não existe em `appsettings.json` nem no Compose.

Para relay, preencha também os placeholders, sem versionar `.env`:

```dotenv
DELIVERY_MODE=SmtpRelay
SMTP_RELAY_HOST=relay.exemplo.com
SMTP_RELAY_PORT=587
SMTP_RELAY_USERNAME=CHANGE_ME
SMTP_RELAY_PASSWORD=CHANGE_ME
SMTP_RELAY_FROM_ADDRESS=remetente-autorizado@exemplo.com
SMTP_RELAY_FROM_NAME=AnInfo Mail Server
SMTP_RELAY_USE_STARTTLS=true
SMTP_RELAY_USE_SSL=false
SMTP_RELAY_PRESERVE_ORIGINAL_FROM=false
SMTP_RELAY_TIMEOUT_SECONDS=30
```

Portas padrão:

- SMTP: `127.0.0.1:2525`.
- HTTP/health: `127.0.0.1:8085`.
- PostgreSQL no Windows: `127.0.0.1:5433` encaminhado ao container `postgres:5432`.

Se uma porta estiver ocupada, altere `POSTGRES_HOST_PORT`, `SMTP_HOST_PORT` ou `HEALTH_HOST_PORT` no `.env`.

## Executar tudo em Docker

```powershell
docker compose up -d --build
docker compose ps
Invoke-WebRequest http://127.0.0.1:8085/health
dotnet run --project .\src\AnInfo.MailServer.TestClient
docker compose logs -f mailserver
```

O Compose cria a rede privada `mailserver_private` e o volume nomeado `postgres_data`. O MailServer usa o hostname Docker `postgres`, aguarda o healthcheck `pg_isready` ficar saudável e então aplica migrations na inicialização.

Parar sem apagar o banco:

```powershell
docker compose down
```

Apagar explicitamente containers e dados (destrutivo):

```powershell
docker compose down -v
```

## Desenvolvimento: Host fora do Docker

Suba somente o banco:

```powershell
docker compose up -d postgres
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5433;Database=aninfo_mail;Username=aninfo;Password=$env:POSTGRES_PASSWORD"
dotnet run --project .\src\AnInfo.MailServer.Host
```

Em Docker, o Compose fornece:

```text
Host=postgres;Port=5432;Database=aninfo_mail;Username=aninfo;Password=<POSTGRES_PASSWORD>
```

Configurações ASP.NET usam o padrão `__`, especialmente `ConnectionStrings__DefaultConnection`, `SmtpServer__Port` e `Health__Url`.

## Restore, build, migrations e publish

```powershell
dotnet restore
dotnet build
dotnet tool restore
dotnet tool run dotnet-ef database update --project .\src\AnInfo.MailServer.Infrastructure --startup-project .\src\AnInfo.MailServer.Host
dotnet publish .\src\AnInfo.MailServer.Host -c Release -o .\publish\host
```

A migration PostgreSQL inicial está versionada. Para uma nova migration:

```powershell
dotnet tool run dotnet-ef migrations add NomeDaMigration --project .\src\AnInfo.MailServer.Infrastructure --startup-project .\src\AnInfo.MailServer.Host --output-dir Migrations
```

## Testes PostgreSQL

O teste cria um banco exclusivo, aplica migrations, inicia o SMTP numa porta efêmera, envia por MailKit, valida a mensagem e remove o banco de teste. Ele não usa serviços externos.

```powershell
docker compose up -d postgres
$env:ANINFO_TEST_POSTGRES = "Host=localhost;Port=5433;Database=postgres;Username=aninfo;Password=$env:POSTGRES_PASSWORD"
dotnet test
```

## TestClient e inspeção

Enviar a mensagem padrão:

```powershell
dotnet run --project .\src\AnInfo.MailServer.TestClient
```

Argumentos disponíveis: `--from`, `--to`, `--subject`, `--body` e `--html`. O destinatário padrão existe somente no TestClient.

```powershell
dotnet run --project .\src\AnInfo.MailServer.TestClient -- `
  --from "remotewakedesk@aninfocloud.com" `
  --to "devadrianonobrega26@gmail.com" `
  --subject "Teste AnInfo MailServer - SMTP Relay" `
  --body "Este e-mail foi enviado pelo AnInfo MailServer através do SMTP Relay."

dotnet run --project .\src\AnInfo.MailServer.TestClient -- `
  --to "devadrianonobrega26@gmail.com" `
  --subject "Teste HTML" `
  --body "<h1>AnInfo MailServer</h1><p>Servidor SMTP funcionando.</p>" `
  --html
```

Inspecionar a última mensagem com Npgsql:

```powershell
$env:MAILSERVER_CONNECTION_STRING = "Host=localhost;Port=5433;Database=aninfo_mail;Username=aninfo;Password=$env:POSTGRES_PASSWORD"
dotnet run --project .\src\AnInfo.MailServer.TestClient -- --inspect
```

## DBeaver

Use uma conexão PostgreSQL com:

- Host: `localhost`
- Port: `5433` (ou `POSTGRES_HOST_PORT` configurada)
- Database: `aninfo_mail`
- Username: `aninfo`
- Password: valor local de `POSTGRES_PASSWORD`
- SSL: desabilitado somente para este desenvolvimento local.

Em produção, remova a seção `ports` do serviço `postgres`. O banco deve permanecer apenas na rede Docker privada e nunca ser publicado na Internet.

## Segurança, logs e limitações

SMTP/HTTP/PostgreSQL são publicados apenas em `127.0.0.1` pelo Compose. O bind SMTP `0.0.0.0` ocorre somente dentro do container e exige a opção de desenvolvimento explícita; a porta publicada continua restrita ao loopback do host. Senhas e corpos não são registrados. Logs ficam em `/app/logs` no container e no console (`docker compose logs`).

Ainda não estão implementados: entrega direta por MX para Gmail/Outlook, DNS, MX, SPF, DKIM, DMARC, porta 25 pública, TLS público, SMTP público ou autenticação pública.

## Troubleshooting do relay

- `Pending`: consulte `NextAttemptAt`, `LastError` e a última `DeliveryAttempt`; pode ser 4xx, DNS, timeout ou conexão.
- `Failed` com 535: usuário, senha ou método de autenticação rejeitado; corrija o secret antes de reenfileirar.
- `Failed` com 5xx: remetente/destinatário/política rejeitados permanentemente.
- Erro TLS: confira hostname, porta, relógio e cadeia do certificado; não desabilite validação.
- Porta 587: STARTTLS. Porta 465: TLS implícito. Não habilite ambos.
- Logs: `docker compose logs -f mailserver`. Password, MIME completo e Authorization não são registrados.
