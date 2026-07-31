using System.Reflection;
using Win7POS.Core.Images;
using Win7POS.Data.Images;

namespace Win7POS.Core.Tests.Images;

[TestClass]
public sealed class ProductImagePipelineArchitectureTests
{
    [TestMethod]
    public void CoreImageContract_DoesNotExposeWpfHttpOrSecretBearingTypes()
    {
        AssertPublicSurfaceIsOfflineAndPlatformNeutral(
            typeof(ProductImageReference).Assembly,
            "Win7POS.Core.Images");
    }

    [TestMethod]
    public void DataImageCache_DoesNotExposeNetworkOrSignedUrlTypes()
    {
        AssertPublicSurfaceIsOfflineAndPlatformNeutral(
            typeof(ProductImageDiskCache).Assembly,
            "Win7POS.Data.Images");
    }

    [TestMethod]
    public void StreamProvider_IsLocalByteBoundaryNotHttpContract()
    {
        var method = typeof(IProductImageStreamProvider)
            .GetMethod(nameof(IProductImageStreamProvider.OpenReadAsync));
        Assert.IsNotNull(method);
        Assert.AreEqual(
            typeof(Task<Stream>),
            method.ReturnType);
        Assert.IsTrue(method.GetParameters().All(parameter =>
            parameter.ParameterType != typeof(Uri) &&
            !string.Equals(
                parameter.ParameterType.FullName,
                "System.Net.Http.HttpClient",
                StringComparison.Ordinal)));
    }

    private static void AssertPublicSurfaceIsOfflineAndPlatformNeutral(
        Assembly assembly,
        string namespacePrefix)
    {
        var types = assembly.GetTypes()
            .Where(type =>
                type.IsPublic &&
                type.Namespace?.StartsWith(
                    namespacePrefix,
                    StringComparison.Ordinal) == true)
            .ToArray();
        Assert.IsTrue(types.Length > 0);

        foreach (var type in types)
        {
            AssertSafe(type, type.FullName ?? type.Name);
            foreach (var property in type.GetProperties(
                         BindingFlags.Instance |
                         BindingFlags.Static |
                         BindingFlags.Public))
            {
                AssertSafe(property.PropertyType, type.Name + "." + property.Name);
            }

            foreach (var method in type.GetMethods(
                         BindingFlags.Instance |
                         BindingFlags.Static |
                         BindingFlags.Public |
                         BindingFlags.DeclaredOnly))
            {
                AssertSafe(method.ReturnType, type.Name + "." + method.Name);
                foreach (var parameter in method.GetParameters())
                {
                    AssertSafe(
                        parameter.ParameterType,
                        type.Name + "." + method.Name + "(" + parameter.Name + ")");
                }
            }
        }
    }

    private static void AssertSafe(Type type, string member)
    {
        var inspected = type;
        if (inspected.IsByRef || inspected.IsArray)
        {
            inspected = inspected.GetElementType()!;
        }

        if (inspected.IsGenericType)
        {
            foreach (var argument in inspected.GetGenericArguments())
            {
                AssertSafe(argument, member);
            }
        }

        var fullName = inspected.FullName ?? inspected.Name;
        Assert.IsFalse(
            fullName.StartsWith("System.Windows", StringComparison.Ordinal) ||
            fullName.StartsWith("System.Net.Http", StringComparison.Ordinal),
            $"Forbidden platform/network type {fullName} exposed by {member}.");
        Assert.IsFalse(
            member.Contains("SignedUrl", StringComparison.OrdinalIgnoreCase) ||
            member.Contains("UploadUrl", StringComparison.OrdinalIgnoreCase) ||
            member.Contains("AccessToken", StringComparison.OrdinalIgnoreCase),
            $"Secret-bearing surface exposed by {member}.");
    }
}
