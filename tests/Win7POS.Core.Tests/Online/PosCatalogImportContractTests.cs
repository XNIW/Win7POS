using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosCatalogImportContractTests
{
    [TestMethod]
    public void CatalogImportPublicContract_DoesNotContainDirectSupabaseTerms()
    {
        var forbidden = new[] { "supabase", "service_role", "supabaseUrl", "supabaseKey", "SUPABASE_SERVICE_ROLE_KEY" };
        var types = typeof(PosCatalogImportRequest).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "Win7POS.Core.Online" && type.Name.Contains("CatalogImport", StringComparison.Ordinal));

        foreach (var type in types)
        {
            AssertNoForbidden(type.Name, forbidden);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                AssertNoForbidden(property.Name, forbidden);
                var memberName = property.GetCustomAttribute<DataMemberAttribute>()?.Name;
                if (!string.IsNullOrWhiteSpace(memberName))
                {
                    AssertNoForbidden(memberName, forbidden);
                }
            }
        }
    }

    private static void AssertNoForbidden(string value, IEnumerable<string> forbidden)
    {
        foreach (var marker in forbidden)
        {
            Assert.IsFalse((value ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase), "Forbidden marker found: " + value);
        }
    }
}
