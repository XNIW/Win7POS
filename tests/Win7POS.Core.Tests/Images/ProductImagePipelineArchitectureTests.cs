using System.Reflection;
using Win7POS.Core.Images;
using Win7POS.Core.Models;
using Win7POS.Data.Images;
using Win7POS.Data.Online;

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

    [TestMethod]
    public void DurableProductImageSurfaces_UseReferencesScopesAndOpaqueStagingOnly()
    {
        var productImageMembers = typeof(ProductDetailsRow)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.Name.Contains(
                "Image",
                StringComparison.Ordinal))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "PrimaryImageUpdatedAt",
                "PrimaryImageVersionId"
            },
            productImageMembers.Select(property => property.Name).ToArray());
        Assert.IsTrue(productImageMembers.All(property =>
            property.PropertyType == typeof(string)));

        var forbiddenDurableNames = new[]
        {
            "Authorization",
            "Capability",
            "Credential",
            "Secret",
            "SignedUrl",
            "Token",
            "UploadUrl"
        };
        var durableTypes = new[]
        {
            typeof(ProductImageOperationRow),
            typeof(ProductImageReplaceEnqueueRequest),
            typeof(ProductImageRemoveEnqueueRequest),
            typeof(ProductImageStagedVariant)
        };
        foreach (var property in durableTypes.SelectMany(type =>
                     type.GetProperties(BindingFlags.Instance | BindingFlags.Public)))
        {
            Assert.IsFalse(
                forbiddenDurableNames.Any(marker => property.Name.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase)),
                "Secret-bearing durable member: " + property.Name);
            Assert.AreNotEqual(typeof(Uri), property.PropertyType);
        }

        CollectionAssert.AreEqual(
            new[] { "Bytes", "Height", "Identity", "Sha256", "Width" },
            typeof(ProductImageStagedVariant)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var activeResolver = typeof(ProductImageCacheScopeStore).GetMethod(
            nameof(ProductImageCacheScopeStore.ResolveActiveAsync));
        Assert.IsNotNull(activeResolver);
        CollectionAssert.AreEqual(
            new[] { "staffId", "shopId", "cancellationToken" },
            activeResolver.GetParameters()
                .Select(parameter => parameter.Name)
                .ToArray());
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
