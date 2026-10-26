using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bag;

internal enum FailureKind
{
    ZipNotFound,
    ZipInvalid,
    ConversationsMissing,
    ConversationsMultiple,
    ExportInvalid,
    DatabaseIncompatible,
    DatabaseBusy,
    FileAccess,
    SourceConflict,
    SourceChanged,
    SealInvalid,
    SealBusy,
    SealChanged,
    TargetMissing,
    TargetAmbiguous,
    ConfigurationInvalid,
    OperationFailed
}

/// <summary>Maps failures to fixed public messages without exposing input or paths.</summary>
internal static class BagErrors
{
    private static readonly object CategoryKey = new();

    public static bool IsHandled(Exception exception)
    {
        return exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or SqliteException or ArgumentException or InvalidOperationException
            or NotSupportedException;
    }

    public static T Tag<T>(T exception, FailureKind kind) where T : Exception
    {
        exception.Data[CategoryKey] = kind;
        return exception;
    }

    public static InvalidDataException InvalidData(FailureKind kind)
    {
        // InvalidDataException is sealed. Keep its type for direct service callers.
        return Tag(new InvalidDataException(GetMessage(kind)), kind);
    }

    public static FailureKind Classify(Exception exception)
    {
        if (exception.Data[CategoryKey] is FailureKind kind)
        {
            return kind;
        }

        return exception switch
        {
            SqliteException { SqliteErrorCode: 5 or 6 } => FailureKind.DatabaseBusy,
            SqliteException { SqliteErrorCode: 1 or 11 or 26 } => FailureKind.DatabaseIncompatible,
            SqliteException { SqliteErrorCode: 19 } => FailureKind.ExportInvalid,
            SqliteException => FailureKind.FileAccess,
            InvalidDataException or JsonException or DecoderFallbackException => FailureKind.ExportInvalid,
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException => FailureKind.FileAccess,
            _ => FailureKind.OperationFailed
        };
    }

    public static string GetMessage(Exception exception)
    {
        return GetMessage(Classify(exception));
    }

    public static string GetMessage(FailureKind kind)
    {
        return kind switch
        {
            FailureKind.ZipNotFound => "ZIP not found.",
            FailureKind.ZipInvalid => "Not a valid ZIP.",
            FailureKind.ConversationsMissing => "conversations.json not found.",
            FailureKind.ConversationsMultiple => "Multiple conversations.json entries.",
            FailureKind.ExportInvalid => "Unsupported export format.",
            FailureKind.DatabaseIncompatible => "Incompatible database.",
            FailureKind.DatabaseBusy => "Database is in use.",
            FailureKind.FileAccess => "File access failed.",
            FailureKind.SourceConflict => "Source and database must differ.",
            FailureKind.SourceChanged => "Source changed during import.",
            FailureKind.SealInvalid => "Invalid seal file.",
            FailureKind.SealBusy => "Seal file is in use.",
            FailureKind.SealChanged => "Seal file changed during update.",
            FailureKind.TargetMissing => "Seal target not found.",
            FailureKind.TargetAmbiguous => "Seal target is ambiguous. Specify a room ID.",
            FailureKind.ConfigurationInvalid => "Invalid configuration.",
            _ => "Operation failed."
        };
    }
}
