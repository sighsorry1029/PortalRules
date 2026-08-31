using System;

namespace PortalRules;

/// <summary>
/// A client-localized message contract for server-to-client UI responses.
/// The server sends only a stable PortalRules token and bounded arguments.
/// </summary>
internal readonly struct PortalRulesMessage
{
    private const string TokenPrefix =
        "$" + PortalRulesLocalization.OwnedTokenPrefix;
    private const int MaximumTokenLength = 128;
    private const int MaximumArgumentCount = 8;
    private const int MaximumArgumentLength = 512;

    internal static readonly PortalRulesMessage Empty = new("", Array.Empty<string>());

    internal readonly string Token;
    internal readonly string[] Arguments;

    internal bool IsEmpty => Token.Length == 0;

    internal PortalRulesMessage(string token, params string[] arguments)
    {
        Token = NormalizeToken(token);
        Arguments = SanitizeArguments(arguments);
    }

    internal string Localize()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        string[] localizedArguments = new string[Arguments.Length];
        for (int index = 0; index < Arguments.Length; index++)
        {
            string argument = Arguments[index];
            localizedArguments[index] =
                PortalRulesLocalization.IsAccessModeToken(argument)
                ? PortalRulesLocalization.Translate(argument)
                : argument.RemoveRichTextTags();
        }

        return PortalRulesLocalization.Translate(Token, localizedArguments);
    }

    internal void Write(ZPackage package)
    {
        package.Write(Token);
        package.Write(Arguments.Length);
        foreach (string argument in Arguments)
        {
            package.Write(argument);
        }
    }

    internal static bool TryRead(
        ZPackage package,
        out PortalRulesMessage message)
    {
        message = Empty;
        string token = package.ReadString();
        int argumentCount = package.ReadInt();
        if (token.Length == 0 && argumentCount == 0)
        {
            message = Empty;
            return true;
        }

        if (!IsValidToken(token) ||
            argumentCount < 0 ||
            argumentCount > MaximumArgumentCount)
        {
            return false;
        }

        string[] arguments = new string[argumentCount];
        for (int index = 0; index < argumentCount; index++)
        {
            string argument = package.ReadString();
            if (!IsValidArgument(argument))
            {
                return false;
            }

            arguments[index] = argument;
        }

        message = new PortalRulesMessage(token, arguments);
        return true;
    }

    private static string NormalizeToken(string? token)
    {
        string normalized = (token ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (!normalized.StartsWith("$", StringComparison.Ordinal))
        {
            normalized = "$" + normalized;
        }

        return IsValidToken(normalized) ? normalized : string.Empty;
    }

    private static string[] SanitizeArguments(string[]? arguments)
    {
        if (arguments == null || arguments.Length == 0)
        {
            return Array.Empty<string>();
        }

        int count = Math.Min(arguments.Length, MaximumArgumentCount);
        string[] sanitized = new string[count];
        for (int index = 0; index < count; index++)
        {
            string value = arguments[index] ?? string.Empty;
            if (value.Length > MaximumArgumentLength)
            {
                value = value.Substring(0, MaximumArgumentLength);
            }

            sanitized[index] = RemoveControlCharacters(value);
        }

        return sanitized;
    }

    private static bool IsValidToken(string token)
    {
        if (token.Length <= TokenPrefix.Length ||
            token.Length > MaximumTokenLength ||
            !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = TokenPrefix.Length; index < token.Length; index++)
        {
            char character = token[index];
            if (character != '_' &&
                (character < 'a' || character > 'z') &&
                (character < '0' || character > '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidArgument(string argument)
    {
        return argument.Length <= MaximumArgumentLength &&
               argument.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
    }

    private static string RemoveControlCharacters(string value)
    {
        char[]? buffer = null;
        int length = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsControl(character))
            {
                buffer ??= value.ToCharArray();
                continue;
            }

            if (buffer != null)
            {
                buffer[length] = character;
            }

            length++;
        }

        return buffer == null
            ? value
            : new string(buffer, 0, length);
    }
}
