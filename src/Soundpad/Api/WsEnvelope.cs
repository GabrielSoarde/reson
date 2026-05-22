namespace Soundpad.Api;

public record WsEnvelope(string Type, string? OriginId, object Payload);
