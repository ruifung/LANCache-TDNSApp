using System.Collections.ObjectModel;
using System.Net;
using System.Reflection;
using System.Text.Json;
using DnsServerCore.ApplicationCommon;
using Moq;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCache.Tests;

public class AppTests
{
    private Mock<IDnsServer> _mockDnsServer;
    private App _app;
    private string _tempFolder;

    [SetUp]
    public void Setup()
    {
        _mockDnsServer = new Mock<IDnsServer>();
        _tempFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempFolder);
        
        _mockDnsServer.Setup(s => s.ApplicationFolder).Returns(_tempFolder);
        _mockDnsServer.Setup(s => s.WriteLog(It.IsAny<string>()));
        _mockDnsServer.Setup(s => s.ServerDomain).Returns("ns1.example.com");
        
        _app = new App();
    }

    [TearDown]
    public void TearDown()
    {
        _app.Dispose();
        if (Directory.Exists(_tempFolder))
        {
            Directory.Delete(_tempFolder, true);
        }
    }

    private async Task InitializeApp(string configJson)
    {
        // Set _domainsLastUpdated to now to skip UpdateLanCacheDomains which tries to use HttpClient
        var field = typeof(App).GetField("_domainsLastUpdated", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(_app, DateTime.Now);
        
        await _app.InitializeAsync(_mockDnsServer.Object, configJson);
    }

    private void SetPrivateField(string fieldName, object value)
    {
        var field = typeof(App).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(_app, value);
    }

    [Test]
    public async Task InitializeAsync_WithValidConfig_LoadsCorrectly()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" }
        };
        string json = JsonSerializer.Serialize(config);

        await InitializeApp(json);

        // We can check if it initialized by trying to process a request and seeing if it doesn't throw.
        // Or check private fields via reflection if absolutely necessary.
        var configField = typeof(App).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance);
        var appConfig = configField?.GetValue(_app);
        Assert.That(appConfig, Is.Not.Null);
    }

    [Test]
    public async Task ProcessRequestAsync_WhenDisabled_ReturnsNull()
    {
        var config = new
        {
            lanCacheEnabled = false,
            operatingMode = "Authoritative"
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("steam.cache.example.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task ProcessRequestAsync_WithDirectMatch_ReturnsARecord()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" },
            recordTtl = 3600
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        // Inject some domains
        var domains = new Dictionary<string, string> { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", new ReadOnlyDictionary<string, string>(domains));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("content1.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Not.Null);
        Assert.That(response.RCODE, Is.EqualTo(DnsResponseCode.NoError));
        Assert.That(response.Answer.Count, Is.EqualTo(1));
        var aRecord = response.Answer[0].RDATA as DnsARecordData;
        Assert.That(aRecord, Is.Not.Null);
        Assert.That(aRecord!.Address, Is.EqualTo(IPAddress.Parse("192.168.1.10")));
    }

    [Test]
    public async Task ProcessRequestAsync_WithWildcardMatch_ReturnsARecord()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        // Inject wildcard domain
        var wildcardDomains = new Dictionary<string, string> { { "steampowered.com", "steam" } };
        SetPrivateField("_wildcardDomains", new ReadOnlyDictionary<string, string>(wildcardDomains));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("anything.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Not.Null);
        Assert.That(response.Answer.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessRequestAsync_WhenDomainNotCached_ReturnsNull()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("google.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task ProcessRequestAsync_WithWhitelist_AllowsWhitelisted()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" },
            enabledCaches = new[] { "steam" }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var domains = new Dictionary<string, string> { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", new ReadOnlyDictionary<string, string>(domains));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("content1.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Not.Null);
    }

    [Test]
    public async Task ProcessRequestAsync_WithBlacklist_BlocksBlacklisted()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" },
            disabledCaches = new[] { "steam" }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var domains = new Dictionary<string, string> { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", new ReadOnlyDictionary<string, string>(domains));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("content1.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task ProcessRequestAsync_WithCacheAddressOverride_ReturnsOverriddenAddress()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" },
            cacheAddresses = new Dictionary<string, List<string>> { { "steam", new List<string> { "192.168.1.20" } } }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var domains = new Dictionary<string, string> { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", new ReadOnlyDictionary<string, string>(domains));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("content1.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Not.Null);
        var aRecord = response!.Answer[0].RDATA as DnsARecordData;
        Assert.That(aRecord, Is.Not.Null);
        Assert.That(aRecord!.Address, Is.EqualTo(IPAddress.Parse("192.168.1.20")));
    }

    [Test]
    public async Task ProcessRequestAsync_InBlockingMode_InterceptsAndReturnsNoError()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Blocking",
            globalCacheAddresses = new[] { "192.168.1.10" }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var domains = new Dictionary<string, string> { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", new ReadOnlyDictionary<string, string>(domains));

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("content1.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await ((IDnsRequestBlockingHandler)_app).ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234));

        Assert.That(response, Is.Not.Null);
        Assert.That(response.RCODE, Is.EqualTo(DnsResponseCode.NoError));
        Assert.That(response.Answer.Count, Is.EqualTo(1));
    }
}
