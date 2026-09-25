namespace Win7POS.Data.Repositories
{
    internal static class SqliteLike
    {
        // Every caller must use LIKE ... ESCAPE '!'. Escape the escape character first.
        internal static string ContainsPattern(string value) =>
            "%" + (value ?? string.Empty).Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
    }
}
