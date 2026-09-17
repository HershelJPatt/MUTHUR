using System.CommandLine;
using System.Text;
using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Cli.Infrastructure;

public static class Output
{
    /// <summary>Success bodies go to stdout, error bodies to stderr; returns the process exit code.</summary>
    public static int Emit(ParseResult parse, ApiResult result)
    {
        var writer = result.IsSuccess ? Console.Out : Console.Error;
        if (result.Body.Length > 0)
            writer.WriteLine(parse.GetValue(Globals.Pretty) ? Indent(result.Body) : result.Body);
        return result.ExitCode;
    }

    public static int Error(string code, string message, int exitCode = ExitCodes.Error)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(code, message), MuthurJsonContext.Default.ErrorResponse));
        return exitCode;
    }

    private static string Indent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                doc.WriteTo(w);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
