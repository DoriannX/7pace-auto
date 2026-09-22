#nullable enable
using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SeptPaceAuto.Services;

/// <summary>Appel adressé au collecteur : la méthode du contrat et ses paramètres bruts.</summary>
internal readonly record struct AgentCall(long Id, string Method, string Parameters);

/// <summary>
/// Réponse du collecteur. <see cref="Payload"/> porte le résultat JSON quand l'appel a
/// abouti, et la phrase à afficher sinon.
/// </summary>
internal readonly record struct AgentReply(long Id, bool Ok, string Payload, string Kind);

/// <summary>
/// Protocole du tuyau : une ligne JSON par message, en UTF-8, terminée par un saut de ligne.
///
/// Le format est versionné et vérifié des deux côtés. Un message d'une autre version, ou
/// illisible, est refusé avec une phrase explicite plutôt que deviné : mieux vaut un refus
/// franc qu'une journée écrite de travers.
/// </summary>
internal static class AgentWire
{
    /// <summary>Erreur métier : le message est déjà la phrase française à afficher.</summary>
    public const string KindDomain = "domain";

    /// <summary>Message mal formé ou version incompatible.</summary>
    public const string KindProtocol = "protocol";

    /// <summary>Panne inattendue du collecteur.</summary>
    public const string KindInternal = "internal";

    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Encode(AgentCall call)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", AgentEndpoint.Protocol);
            writer.WriteNumber("id", call.Id);
            writer.WriteString("method", call.Method ?? string.Empty);
            writer.WritePropertyName("params");
            writer.WriteRawValue(Compact(call.Parameters));
            writer.WriteEndObject();
        }
        return Utf8.GetString(buffer.WrittenSpan);
    }

    public static string Encode(AgentReply reply)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", AgentEndpoint.Protocol);
            writer.WriteNumber("id", reply.Id);
            writer.WriteBoolean("ok", reply.Ok);
            if (reply.Ok)
            {
                writer.WritePropertyName("result");
                writer.WriteRawValue(Compact(reply.Payload));
            }
            else
            {
                writer.WriteString("error", reply.Payload ?? string.Empty);
                writer.WriteString("kind", reply.Kind ?? KindInternal);
            }
            writer.WriteEndObject();
        }
        return Utf8.GetString(buffer.WrittenSpan);
    }

    public static bool TryReadCall(string line, out AgentCall call, out string error)
    {
        call = default;
        if (!TryParse(line, out var root, out error)) return false;

        using (root)
        {
            if (!Version(root.RootElement, out error)) return false;

            var method = root.RootElement.TryGetProperty("method", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(method))
            {
                error = "Appel sans méthode : le terminal et le collecteur ne parlent pas la même langue.";
                return false;
            }

            var parameters = root.RootElement.TryGetProperty("params", out var raw) && raw.ValueKind == JsonValueKind.Object
                ? raw.GetRawText()
                : "{}";

            call = new AgentCall(Identifier(root.RootElement), method!, parameters);
            return true;
        }
    }

    public static bool TryReadReply(string line, out AgentReply reply, out string error)
    {
        reply = default;
        if (!TryParse(line, out var root, out error)) return false;

        using (root)
        {
            if (!Version(root.RootElement, out error)) return false;

            var identifier = Identifier(root.RootElement);
            var ok = root.RootElement.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (ok)
            {
                var result = root.RootElement.TryGetProperty("result", out var payload) ? payload.GetRawText() : "{}";
                reply = new AgentReply(identifier, true, result, string.Empty);
                return true;
            }

            var message = root.RootElement.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : "Le collecteur a refusé l’appel sans préciser pourquoi.";
            var kind = root.RootElement.TryGetProperty("kind", out var node) && node.ValueKind == JsonValueKind.String
                ? node.GetString() ?? KindInternal
                : KindInternal;
            reply = new AgentReply(identifier, false, message, kind);
            return true;
        }
    }

    private static bool TryParse(string line, out JsonDocument document, out string error)
    {
        document = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "Message vide reçu sur le tuyau du collecteur.";
            return false;
        }
        try
        {
            var parsed = JsonDocument.Parse(line);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                error = "Message illisible reçu sur le tuyau du collecteur.";
                return false;
            }
            document = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "Message illisible reçu sur le tuyau du collecteur.";
            return false;
        }
    }

    private static bool Version(JsonElement root, out string error)
    {
        error = string.Empty;
        var version = root.TryGetProperty("v", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
        if (version == AgentEndpoint.Protocol) return true;

        error = $"Protocole incompatible : le collecteur parle la version {AgentEndpoint.Protocol}, " +
            $"la version {version} a été reçue. Ferme le terminal, relance l’installation puis réessaie.";
        return false;
    }

    private static long Identifier(JsonElement root) =>
        root.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>
    /// Une ligne ne doit contenir aucun saut de ligne : un JSON indenté est recompacté
    /// avant d'être recopié tel quel dans l'enveloppe.
    /// </summary>
    private static string Compact(string? json)
    {
        var text = string.IsNullOrWhiteSpace(json) ? "{}" : json!;
        if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0) return text;

        using var document = JsonDocument.Parse(text);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            document.WriteTo(writer);
        }
        return Utf8.GetString(buffer.WrittenSpan);
    }
}
