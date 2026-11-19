namespace ProGuard.Core.Models;

/// <summary>
/// A stored rule that records who saved it. The engine applies rules as SYSTEM, so a rule saved by a standard user
/// acts only on that user's own processes (see <see cref="RuleAuthorship.IsScopedToAuthor"/>); one saved by an
/// administrator or by the engine itself acts on every process of its name.
/// <para>
/// Neither value ever comes from a client: the engine sets both from the caller's token when it stores the rule, and
/// they have no counterpart in the DTOs the UI sends.
/// </para>
/// </summary>
public interface IAuthoredRule
{
    /// <summary>
    /// SID of the account that last saved the rule. Null for rules stored before authors were recorded, which count as
    /// administrator rules (until then only administrators used ProGuard on the machines it ran on).
    /// </summary>
    string? AuthorSid { get; set; }

    /// <summary>
    /// Whether that account was an administrator (an elevated token, SYSTEM, or the engine itself) when it saved the
    /// rule. True by default, which is also what the schema upgrade gives rules that existed before the column did.
    /// </summary>
    bool AuthorIsAdministrator { get; set; }
}

public static class RuleAuthorship
{
    /// <summary>
    /// The Null SID: recorded as the author of a standard user's rule when the caller's own SID couldn't be read, so
    /// the rule matches no process rather than every process.
    /// </summary>
    public const string NobodySid = "S-1-0-0";

    /// <summary>True when <paramref name="rule"/> may act only on processes its author owns.</summary>
    public static bool IsScopedToAuthor(this IAuthoredRule rule) => !rule.AuthorIsAdministrator && rule.AuthorSid != null;

    /// <summary>Whether <paramref name="sid"/> (a caller) is the recorded author of a rule scoped to its author.</summary>
    public static bool IsAuthor(this IAuthoredRule rule, string? sid) =>
        rule.IsScopedToAuthor() && sid != null && string.Equals(rule.AuthorSid, sid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records who is saving <paramref name="rule"/>.</summary>
    public static void SetAuthor(this IAuthoredRule rule, string? sid, bool isAdministrator)
    {
        rule.AuthorIsAdministrator = isAdministrator;
        rule.AuthorSid = isAdministrator ? sid : (string.IsNullOrEmpty(sid) ? NobodySid : sid);
    }

    /// <summary>Copies the author of <paramref name="from"/> onto <paramref name="to"/> (a rule kept unchanged through a replace).</summary>
    public static void CopyAuthor(this IAuthoredRule to, IAuthoredRule from)
    {
        to.AuthorSid = from.AuthorSid;
        to.AuthorIsAdministrator = from.AuthorIsAdministrator;
    }
}
