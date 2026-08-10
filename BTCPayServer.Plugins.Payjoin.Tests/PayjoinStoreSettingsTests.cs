using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinStoreSettingsTests
{
    private const string TestXpub = "xpub661MyMwAqRbcFtXgS5sYJABqqG9YLmC4Q1Rdap9gSE8NqtwybGhePY2gZ29ESFjqJoCu1Rupje8YtGqsefD265TMg7usUDFdp6W1EGMcet8";

    [Fact]
    public void NewSettingsHasExpectedDefaults()
    {
        var settings = new PayjoinStoreSettings();

        Assert.True(settings.PayjoinV2Enabled);
        Assert.Equal(
            [
                new Uri("https://payjo.in/"),
                new Uri("https://lets.payjo.in/")
            ],
            PayjoinStoreSettings.DefaultDirectoryUrls);
        Assert.Equal(PayjoinStoreSettings.DefaultDirectoryUrls, settings.GetEffectiveDirectoryUrls());
        Assert.Equal(
            [
                new Uri("https://pj.benalleng.com"),
                new Uri("https://pj.bobspacebkk.com"),
                new Uri("https://payjoin.achow101.com")
            ],
            PayjoinStoreSettings.DefaultOhttpRelayUrls);
        Assert.Equal(PayjoinStoreSettings.DefaultOhttpRelayUrls, settings.GetEffectiveOhttpRelayUrls());
        Assert.Null(settings.ColdWalletDerivationScheme);
    }

    [Fact]
    public void SettingsPreserveAssignedValues()
    {
        var directoryUrls = new[]
        {
            new Uri("https://example.com/directory"),
            new Uri("https://example.com/directory-2")
        };
        var ohttpRelayUrl = new Uri("https://example.com/relay");
        var ohttpRelayUrls = new[]
        {
            ohttpRelayUrl,
            new Uri("https://example.com/relay-2")
        };

        var settings = new PayjoinStoreSettings
        {
            PayjoinV2Enabled = true,
            DirectoryUrls = directoryUrls,
            OhttpRelayUrls = ohttpRelayUrls,
            ColdWalletDerivationScheme = TestXpub
        };
        settings.NormalizeUrlSettings();

        Assert.True(settings.PayjoinV2Enabled);
        Assert.Equal(directoryUrls, settings.GetEffectiveDirectoryUrls());
        Assert.Equal(ohttpRelayUrls, settings.GetEffectiveOhttpRelayUrls());
        Assert.Equal(TestXpub, settings.ColdWalletDerivationScheme);
    }

    [Fact]
    public void EffectiveDirectoryUrlsReturnEmptyWhenNoDirectoriesConfigured()
    {
        var settings = new PayjoinStoreSettings
        {
            DirectoryUrls = null
        };

        Assert.Empty(settings.GetEffectiveDirectoryUrls());
    }

    [Fact]
    public void EffectiveRelayUrlsReturnEmptyWhenNoRelaysConfigured()
    {
        var settings = new PayjoinStoreSettings
        {
            OhttpRelayUrls = null
        };

        Assert.Empty(settings.GetEffectiveOhttpRelayUrls());
    }

    [Fact]
    public void NormalizeRelaySettingsUsesRelayUrlsAsSourceOfTruth()
    {
        var firstRelayUrl = new Uri("https://example.com/relay-1");
        var secondRelayUrl = new Uri("https://example.com/relay-2");
        var settings = new PayjoinStoreSettings
        {
            OhttpRelayUrls = [firstRelayUrl, secondRelayUrl]
        };

        settings.NormalizeUrlSettings();

        Assert.Equal([firstRelayUrl, secondRelayUrl], settings.OhttpRelayUrls);
    }

    [Fact]
    public void ParseOhttpRelayUrlsTextReportsErrorsAndKeepsValidHttpsEntries()
    {
        var parsed = PayjoinStoreSettingsInput.ParseOhttpRelayUrlsTextWithErrors(
            " https://example.com/relay-1 \r\nhttp://example.com/relay-2\nnot-a-url\nhttps://example.com/relay-1\nhttps://example.com/relay-3");

        Assert.Equal(
            [
                new Uri("https://example.com/relay-1"),
                new Uri("https://example.com/relay-3")
            ],
            parsed.Urls);
        Assert.Equal(2, parsed.Errors.Count);
        Assert.Contains(parsed.Errors, error => error.Value == "http://example.com/relay-2");
        Assert.Contains(parsed.Errors, error => error.Value == "not-a-url");
    }

    [Fact]
    public void ParseOhttpRelayUrlsTextReportsActualLineNumbersWhenBlankLinesExist()
    {
        var parsed = PayjoinStoreSettingsInput.ParseOhttpRelayUrlsTextWithErrors(
            "https://example.com/relay-1\n\nnot-a-url");

        var error = Assert.Single(parsed.Errors);
        Assert.Equal(3, error.LineNumber);
        Assert.Equal("not-a-url", error.Value);
    }

    [Fact]
    public void ParseDirectoryUrlsTextReportsErrorsAndKeepsValidHttpsEntries()
    {
        var parsed = PayjoinStoreSettingsInput.ParseDirectoryUrlsTextWithErrors(
            " https://example.com/directory-1 \r\nhttp://example.com/directory-2\nnot-a-url\nhttps://example.com/directory-1\nhttps://example.com/directory-3");

        Assert.Equal(
            [
                new Uri("https://example.com/directory-1"),
                new Uri("https://example.com/directory-3")
            ],
            parsed.Urls);
        Assert.Equal(2, parsed.Errors.Count);
        Assert.Contains(parsed.Errors, error => error.Value == "http://example.com/directory-2");
        Assert.Contains(parsed.Errors, error => error.Value == "not-a-url");
    }

    [Fact]
    public void ParseDirectoryUrlsTextReportsActualLineNumbersWhenBlankLinesExist()
    {
        var parsed = PayjoinStoreSettingsInput.ParseDirectoryUrlsTextWithErrors(
            "https://example.com/directory-1\n\nnot-a-url");

        var error = Assert.Single(parsed.Errors);
        Assert.Equal(3, error.LineNumber);
        Assert.Equal("not-a-url", error.Value);
    }

    [Fact]
    public void DataRejectsNullUrlEntriesAsInvalid()
    {
        var data = JObject.Parse("""
            {
              "directoryUrls": [null, "https://example.com/directory"],
              "ohttpRelayUrls": [null, "https://example.com/relay"]
            }
            """).ToObject<PayjoinStoreSettingsData>()!;

        Assert.Contains(data.GetInvalidDirectoryUrls(), static url => url is null);
        Assert.Contains(data.GetInvalidOhttpRelayUrls(), static url => url is null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    public void DataRejectsFeeRatesOutsideTheSharedBounds(long feeRateSatPerVb)
    {
        var data = new PayjoinStoreSettingsData { MaxFeeRateSatPerVb = feeRateSatPerVb };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            data,
            new ValidationContext(data),
            validationResults,
            validateAllProperties: true);

        Assert.False(isValid);
        var validationResult = Assert.Single(validationResults);
        Assert.Contains(nameof(PayjoinStoreSettingsData.MaxFeeRateSatPerVb), validationResult.MemberNames);
    }

    [Fact]
    public void ViewModelToSettingsPrefersTextFieldsOverUriLists()
    {
        var input = new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            DirectoryUrls = [new Uri("https://fallback.example/directory")],
            DirectoryUrlsText = "https://configured.example/directory",
            OhttpRelayUrls = [new Uri("https://fallback.example/relay")],
            OhttpRelayUrlsText = "https://configured.example/relay",
            LayoutModel = new LayoutModel("Payjoin", "Payjoin")
        };

        var settings = input.ToSettings();

        Assert.Equal([new Uri("https://configured.example/directory")], settings.DirectoryUrls);
        Assert.Equal([new Uri("https://configured.example/relay")], settings.OhttpRelayUrls);
    }

    [Fact]
    public void FormatOhttpRelayUrlsTextWritesOneRelayPerLine()
    {
        var relayUrlsText = PayjoinStoreSettingsInput.FormatOhttpRelayUrlsText(
        [
            new Uri("https://example.com/relay-1"),
            new Uri("https://example.com/relay-2")
        ]);

        Assert.Equal($"https://example.com/relay-1{Environment.NewLine}https://example.com/relay-2", relayUrlsText);
    }

    [Fact]
    public void FormatDirectoryUrlsTextWritesOneDirectoryPerLine()
    {
        var directoryUrlsText = PayjoinStoreSettingsInput.FormatDirectoryUrlsText(
        [
            new Uri("https://example.com/directory-1"),
            new Uri("https://example.com/directory-2")
        ]);

        Assert.Equal($"https://example.com/directory-1{Environment.NewLine}https://example.com/directory-2", directoryUrlsText);
    }

    [Fact]
    public void DataRoundTripsThroughSettings()
    {
        var data = new PayjoinStoreSettingsData
        {
            PayjoinV2Enabled = false,
            DirectoryUrls =
            [
                new Uri("https://example.com/directory"),
                new Uri("https://example.com/directory-2")
            ],
            OhttpRelayUrls =
            [
                new Uri("https://example.com/relay"),
                new Uri("https://example.com/relay-2")
            ],
            ColdWalletDerivationScheme = TestXpub
        };

        var settings = data.ToSettings();
        var roundTripped = PayjoinStoreSettingsData.FromSettings(settings);

        Assert.Equal(data.PayjoinV2Enabled, roundTripped.PayjoinV2Enabled);
        Assert.Equal(data.DirectoryUrls, roundTripped.DirectoryUrls);
        Assert.Equal(data.OhttpRelayUrls, roundTripped.OhttpRelayUrls);
        Assert.Equal(data.ColdWalletDerivationScheme, roundTripped.ColdWalletDerivationScheme);
    }

    [Fact]
    public void ReadSettingsNormalizesPersistedDirectoryAndRelayUrls()
    {
        var blob = new StoreBlob();
        blob.AdditionalData = new JObject
        {
            ["payjoin.settings"] = JToken.FromObject(new
            {
                PayjoinV2Enabled = true,
                DirectoryUrls = new[]
                {
                    "https://example.com/directory",
                    "http://example.com/directory",
                    "https://example.com/directory"
                },
                OhttpRelayUrls = new[]
                {
                    "https://example.com/relay",
                    "http://example.com/relay",
                    "https://example.com/relay"
                }
            })
        };

        var settings = PayjoinStoreSettingsRepository.ReadSettings(blob);

        Assert.Equal([new Uri("https://example.com/directory")], settings.DirectoryUrls);
        Assert.Equal([new Uri("https://example.com/relay")], settings.OhttpRelayUrls);
    }

    [Fact]
    public void NormalizingRepositoryCopyDoesNotMutateOriginalSettings()
    {
        var settings = new PayjoinStoreSettings
        {
            DirectoryUrls = new Uri[]
            {
                new("https://example.com/directory"),
                new("http://example.com/directory")
            },
            OhttpRelayUrls = new Uri[]
            {
                new("https://example.com/relay"),
                new("http://example.com/relay")
            }
        };

        var normalizedSettings = new PayjoinStoreSettings
        {
            PayjoinV2Enabled = settings.PayjoinV2Enabled,
            DirectoryUrls = PayjoinStoreSettings.NormalizeDirectoryUrls(settings.DirectoryUrls),
            OhttpRelayUrls = PayjoinStoreSettings.NormalizeOhttpRelayUrls(settings.OhttpRelayUrls),
            ColdWalletDerivationScheme = settings.ColdWalletDerivationScheme
        };
        normalizedSettings.NormalizeUrlSettings();

        Assert.Equal(2, settings.DirectoryUrls!.Count);
        Assert.Equal(2, settings.OhttpRelayUrls!.Count);
        Assert.Single(normalizedSettings.DirectoryUrls!);
        Assert.Single(normalizedSettings.OhttpRelayUrls!);
    }

    [Fact]
    public void ViewModelRoundTripsThroughSettings()
    {
        var layoutModel = new LayoutModel("Payjoin", "Payjoin");
        var model = new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            PayjoinV2Enabled = false,
            DirectoryUrls =
            [
                new Uri("https://example.com/directory"),
                new Uri("https://example.com/directory-2")
            ],
            OhttpRelayUrls =
            [
                new Uri("https://example.com/relay"),
                new Uri("https://example.com/relay-2")
            ],
            ColdWalletDerivationScheme = TestXpub,
            LayoutModel = layoutModel
        };

        var settings = model.ToSettings();
        var roundTripped = PayjoinStoreSettingsViewModel.FromSettings(model.StoreId, settings, layoutModel);

        Assert.Equal(model.StoreId, roundTripped.StoreId);
        Assert.Equal(model.PayjoinV2Enabled, roundTripped.PayjoinV2Enabled);
        Assert.Equal(model.DirectoryUrls, roundTripped.DirectoryUrls);
        Assert.Equal(PayjoinStoreSettingsInput.FormatDirectoryUrlsText(model.DirectoryUrls), roundTripped.DirectoryUrlsText);
        Assert.Equal(model.OhttpRelayUrls, roundTripped.OhttpRelayUrls);
        Assert.Equal(PayjoinStoreSettingsInput.FormatOhttpRelayUrlsText(model.OhttpRelayUrls), roundTripped.OhttpRelayUrlsText);
        Assert.Equal(model.ColdWalletDerivationScheme, roundTripped.ColdWalletDerivationScheme);
        Assert.Same(layoutModel, roundTripped.LayoutModel);
    }
}
