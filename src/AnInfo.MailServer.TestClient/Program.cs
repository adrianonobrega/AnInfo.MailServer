using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Npgsql;

if (args.Length > 0 && args[0] == "--inspect")
{
    var connectionString = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("MAILSERVER_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Forneça a connection string após --inspect ou em MAILSERVER_CONNECTION_STRING.");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT m."MessageId", m."From", m."Subject", m."Status", r."Address", r."Type",
               a."AttemptNumber", a."Success", a."SmtpStatusCode", a."ErrorType"
        FROM "MailMessages" m JOIN "MailRecipients" r ON r."MailMessageId" = m."Id"
        LEFT JOIN LATERAL (
            SELECT "AttemptNumber", "Success", "SmtpStatusCode", "ErrorType"
            FROM "DeliveryAttempts" WHERE "MailMessageId" = m."Id"
            ORDER BY "AttemptNumber" DESC LIMIT 1
        ) a ON true
        ORDER BY m."CreatedAt" DESC LIMIT 1
        """;
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) { Console.WriteLine("Nenhuma mensagem persistida."); return; }
    var attempt = reader.IsDBNull(6) ? "none" : reader.GetInt32(6).ToString();
    var success = reader.IsDBNull(7) ? "n/a" : reader.GetBoolean(7).ToString();
    var smtpStatus = reader.IsDBNull(8) ? "n/a" : reader.GetInt32(8).ToString();
    var errorType = reader.IsDBNull(9) ? "none" : reader.GetString(9);
    Console.WriteLine($"MessageId={reader.GetString(0)}; From={reader.GetString(1)}; Subject={reader.GetString(2)}; Status={reader.GetString(3)}; Recipient={reader.GetString(4)}; Type={reader.GetString(5)}; Attempt={attempt}; Success={success}; SmtpStatus={smtpStatus}; ErrorType={errorType}");
    return;
}

var arguments = ParseArguments(args);
var host = Environment.GetEnvironmentVariable("MAILSERVER_HOST") ?? "127.0.0.1";
var port = int.TryParse(Environment.GetEnvironmentVariable("MAILSERVER_PORT"), out var configuredPort) ? configuredPort : 2525;
var from = Get(arguments, "from", "remotewakedesk@aninfocloud.com");
var to = Get(arguments, "to", "devadrianonobrega26@gmail.com");
var subject = Get(arguments, "subject", "Teste AnInfo MailServer");
var body = Get(arguments, "body", arguments.ContainsKey("html")
    ? "<h1>AnInfo MailServer</h1><p>Servidor SMTP funcionando.</p>"
    : "Servidor SMTP funcionando.");
var message = new MimeMessage();
message.From.Add(MailboxAddress.Parse(from));
message.To.Add(MailboxAddress.Parse(to));
message.Subject = subject;
message.Body = new TextPart(arguments.ContainsKey("html") ? "html" : "plain") { Text = body };
using var client = new SmtpClient();
await client.ConnectAsync(host, port, SecureSocketOptions.None);
var user = Environment.GetEnvironmentVariable("MAILSERVER_USERNAME");
if (!string.IsNullOrWhiteSpace(user))
    await client.AuthenticateAsync(user, Environment.GetEnvironmentVariable("MAILSERVER_PASSWORD") ?? "");
await client.SendAsync(message);
await client.DisconnectAsync(true);
Console.WriteLine($"Mensagem {message.MessageId} enviada para {host}:{port}.");

static Dictionary<string, string?> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index++)
    {
        var current = values[index];
        if (!current.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Argumento inesperado: {current}");
        var key = current[2..];
        if (key.Equals("html", StringComparison.OrdinalIgnoreCase)) { result[key] = null; continue; }
        if (index + 1 >= values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Informe um valor para --{key}.");
        result[key] = values[++index];
    }
    return result;
}

static string Get(IReadOnlyDictionary<string, string?> values, string key, string fallback) =>
    values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
