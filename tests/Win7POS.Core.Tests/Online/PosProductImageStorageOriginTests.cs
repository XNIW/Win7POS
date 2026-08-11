using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
[DoNotParallelize]
public sealed class PosProductImageStorageOriginTests
{
    [TestMethod]
    public void EnvironmentOrigin_RequiresHttpsRootWithoutCredentialsOrQuery()
    {
        var previous = Environment.GetEnvironmentVariable(
            PosProductImageStorageOrigin.EnvironmentVariable);
        try
        {
            AssertOrigin("https://storage.example.invalid", expected: true);
            AssertOrigin("http://127.0.0.1:54321", expected: true);
            AssertOrigin("http://storage.example.invalid", expected: false);
            AssertOrigin("https://user@storage.example.invalid", expected: false);
            AssertOrigin("https://storage.example.invalid/path", expected: false);
            AssertOrigin("https://storage.example.invalid/?token=secret", expected: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                PosProductImageStorageOrigin.EnvironmentVariable,
                previous);
        }
    }

    private static void AssertOrigin(string value, bool expected)
    {
        Environment.SetEnvironmentVariable(
            PosProductImageStorageOrigin.EnvironmentVariable,
            value);

        var loaded = PosProductImageStorageOrigin.TryLoad(out var origin, out var code);

        Assert.AreEqual(expected, loaded, value);
        Assert.AreEqual(expected ? "success" : "product_image_storage_origin_invalid", code);
        if (expected)
            Assert.AreEqual("/", origin.AbsolutePath);
        else
            Assert.IsNull(origin);
    }
}
