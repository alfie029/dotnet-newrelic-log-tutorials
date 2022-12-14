using System.Reflection.Metadata;
using dotnet_newrelic_log_common.Extensions;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace dotnet_newrelic_log_common.Formatter;

public class StructuredJsonFormatter : ITextFormatter
{
    private const uint MaxMessageLengthInBytes = 32768;
    private const string LinkingMetadataKey = "newrelic.linkingmetadata";
    private readonly JsonValueFormatter _valueFormatter;

    public StructuredJsonFormatter(JsonValueFormatter? valueFormatter = null) =>
        _valueFormatter = valueFormatter ?? new JsonValueFormatter("$type");

    /// <summary>
    ///  Format the log event into the output. Subsequent events will be newline-delimited.
    /// </summary>
    /// <param name="logEvent">The event to format.</param>
    /// <param name="output">The output.</param>
    public void Format(LogEvent logEvent, TextWriter output)
    {
        if (logEvent == null)
            throw new ArgumentNullException(nameof(logEvent));
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        output.WriteObjectStart();

        // "@t":"2022-11-02T06:30:10.5368801Z"
        output.WriteTimestamp(logEvent.Timestamp.UtcDateTime.ToString("O"));
        output.WriteTrailingComma();

        // "@mt":"message template"
        using var templateMessageWriter = new StringWriter();
        JsonValueFormatter.WriteQuotedJsonString(
            logEvent.MessageTemplate.Text.TruncateUnicodeStringByBytes(MaxMessageLengthInBytes)!,
            templateMessageWriter);
        output.WriteMessageTemplate(templateMessageWriter.ToString());
        output.WriteTrailingComma();

        var refProps = logEvent.MessageTemplate.Tokens
            .OfType<PropertyToken>()
            .Where(pt => pt.Format != null)
            .Select(pt =>
            {
                using var ptWriter = new StringWriter();
                pt.Render(logEvent.Properties, ptWriter);
                return ptWriter.ToString();
            })
            .Select(pt =>
            {
                using var propWriter = new StringWriter();
                JsonValueFormatter.WriteQuotedJsonString(pt, propWriter);
                return propWriter.ToString();
            })
            .ToList();
        if (refProps.Any())
        {
            // "@r":["Request starting HTTP/1.1 GET https://host-url/controller - -"]
            output.WriteReferredProperties(refProps);
            output.WriteTrailingComma();
        }

        // "@l": "Warning"
        output.WriteLogLevel(logEvent.Level);
        output.WriteTrailingComma();

        // "level": "Warning"
        output.WriteLogLevel(logEvent.Level, "level");
        output.WriteTrailingComma();

        // "message": "Business run ok with 00:00:01.5684733"
        using var messageWriter = new StringWriter();
        JsonValueFormatter.WriteQuotedJsonString(
            logEvent.MessageTemplate.Render(logEvent.Properties).TruncateUnicodeStringByBytes(MaxMessageLengthInBytes)!,
            messageWriter);
        output.WriteRenderedMessage(messageWriter.ToString());
        output.WriteTrailingComma();

        if (logEvent.Exception != null)
        {
            // "@x":"System.ArgumentNullException: Value cannot be null. (Parameter 'userId')\n   at namespace.method(String userId, String others) in /source_code/code.cs:line 91"
            using var exceptionStringWriter = new StringWriter();
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), exceptionStringWriter);
            output.WriteExceptionMessage(exceptionStringWriter.ToString());
            output.WriteTrailingComma();
        }

        logEvent.Properties
            .Flatten(depth: 2)
            .Select(p => p.Key.FirstOrDefault() == '@'
                ? new KeyValuePair<string, LogEventPropertyValue>($"@{p.Key}", p.Value)
                : p)
            .ToList()
            .ForEach(p =>
            {
                using var propValWriter = new StringWriter();
                _valueFormatter.Format(p.Value, propValWriter);
                output.WriteProperty(p.Key,
                    propValWriter.ToString().TruncateUnicodeStringByBytes(MaxMessageLengthInBytes)!);
                output.WriteTrailingComma();
            });

        // NOTE:
        //   above loop would write an extra trailing comma, if no further properties appended, need remove it by
        //   // output.Write("\b");
        output.WriteAssemblyInfo();
        output.WriteObjectEnd();

        // final write a new line
        output.WriteLine();
    }
}
