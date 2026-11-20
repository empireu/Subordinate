using System.Text.Json;
using Subordinate.Core;
using Subordinate.Core.Configuration;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class ImageConfigurationTests
{
    [Fact]
    public void ImageSettingsParseFromConfig()
    {
        var config = JsonSerializer.Deserialize<SubordinateConfig>("""
            {
              "models": {
                "vision": { "provider": "test", "model": "test-model", "supports_images": true, "max_images": 8 }
              }
            }
            """, SubordinateConfig.JsonOptions)!;

        Assert.True(config.Models["vision"].SupportsImages);
        Assert.Equal(8, config.Models["vision"].MaxImages);
    }

    [Fact]
    public void MissingImageSettingsDisableImages()
    {
        var config = JsonSerializer.Deserialize<SubordinateConfig>("""
            {
              "models": { "default": { "provider": "test", "model": "test-model" } }
            }
            """, SubordinateConfig.JsonOptions)!;

        Assert.False(config.Models["default"].SupportsImages);
        Assert.Null(config.Models["default"].MaxImages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MaxImagesBelowOneFailsWithConfigError(int maxImages)
    {
        var json = $$"""
            {
              "providers": { "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" } },
              "models": {
                "default": { "provider": "test", "model": "test-model", "supports_images": true, "max_images": {{maxImages}} }
              }
            }
            """;

        var exception = CreateRunnerExpectingFailure(json);

        Assert.Equal(ErrorCode.ConfigError, exception.Code);
        Assert.Contains("default", exception.Message, StringComparison.Ordinal);
        Assert.Contains("max_images", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxImagesWithoutSupportsImagesFailsWithConfigError()
    {
        var exception = CreateRunnerExpectingFailure("""
            {
              "providers": { "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" } },
              "models": {
                "default": { "provider": "test", "model": "test-model", "max_images": 4 }
              }
            }
            """);

        Assert.Equal(ErrorCode.ConfigError, exception.Code);
        Assert.Equal("Model \"default\" sets max_images but does not set supports_images.", exception.Message);
    }

    [Fact]
    public void EnabledImageSettingsCarryTheLimit()
    {
        var model = new ModelEntry { Provider = "test", Model = "test-model", SupportsImages = true, MaxImages = 8 };

        var images = RunFactory.CreateImageSettings("vision", model);

        Assert.True(images.Enabled);
        Assert.Equal(8, images.MaxImages);
    }

    [Fact]
    public void EnabledImageSettingsWithoutLimitAreUnlimited()
    {
        var model = new ModelEntry { Provider = "test", Model = "test-model", SupportsImages = true };

        var images = RunFactory.CreateImageSettings("vision", model);

        Assert.True(images.Enabled);
        Assert.Null(images.MaxImages);
    }

    [Fact]
    public void AModelWithoutSupportsImagesGetsTheDisabledSettings()
    {
        var model = new ModelEntry { Provider = "test", Model = "test-model" };

        var images = RunFactory.CreateImageSettings("plain", model);

        Assert.False(images.Enabled);
        Assert.Null(images.MaxImages);
    }

    private static SubordinateException CreateRunnerExpectingFailure(string json)
    {
        var config = JsonSerializer.Deserialize<SubordinateConfig>(json, SubordinateConfig.JsonOptions)!;
        var store = new SessionStore(Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N")));
        var record = new SessionRecord
        {
            Model = new ModelRecord { Alias = "default", Provider = "test", ModelId = "test-model" }
        };

        return Assert.Throws<SubordinateException>(() => RunFactory.CreateRunner(config, store, new SessionPaths(store.SessionsDirectory), record, "run"));
    }
}
