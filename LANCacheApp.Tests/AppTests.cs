using System.IO.Compression;
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

    private delegate bool DomainMatcher(Dictionary<string, string> direct, Dictionary<string, string> wildcard,
        string domain, out string? zone, out string? target);

    private static readonly DomainMatcher MatchDomain = typeof(App)
        .GetMethod("TryFindCacheDomain", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<DomainMatcher>();

    [TestCase("EXAMPLE.COM", "Example.COM", "direct")]
    [TestCase("a.example.com", "Example.COM", "direct")]
    [TestCase("x.deep.example.com", "deep.Example.com", "specific")]
    [TestCase("x.wild.example.com", "Example.COM", "direct")]
    [TestCase("wild.net", null, null)]
    [TestCase("x.wild.net", "Wild.NET", "wild")]
    [TestCase("x.y.wild.net", "Wild.NET", "wild")]
    [TestCase("x.deep.wild.net", "deep.wild.net", "specific-wild")]
    [TestCase("notexample.com", null, null)]
    [TestCase("a.b.c.d.unrelated.org", null, null)]
    [TestCase("localhost", null, null)]
    [TestCase("", null, null)]
    [TestCase("I.EXAMPLE.COM", "Example.COM", "direct")]
    [TestCase("IMAGE.ORG", "image.org", "culture")]
    [TestCase("local", "local", "single-label")]
    public void TryFindCacheDomain_PreservesMatchingPolicy(string domain, string? expectedZone, string? expectedTarget)
    {
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Example.COM"] = "direct", ["deep.Example.com"] = "specific",
            ["image.org"] = "culture", ["local"] = "single-label"
        };
        var wildcard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wild.example.com"] = "losing-wild", ["Wild.NET"] = "wild",
            ["deep.wild.net"] = "specific-wild"
        };
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.That(MatchDomain(direct, wildcard, domain, out var zone, out var target),
                Is.EqualTo(expectedZone is not null));
            Assert.That(zone, Is.EqualTo(expectedZone));
            Assert.That(target, Is.EqualTo(expectedTarget));
            if (zone is not null)
                Assert.That(ReferenceEquals(zone, direct.Keys.Concat(wildcard.Keys).Single(k => k == zone)), Is.True);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
        Assert.That(MatchDomain(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
            domain, out var emptyZone, out var emptyTarget), Is.False);
        Assert.That(emptyZone, Is.Null);
        Assert.That(emptyTarget, Is.Null);
    }

    [Test]
    public async Task ProcessRequestAsync_UsesWinningServiceAndStoredSuffixMetadata()
    {
        await InitializeApp(JsonSerializer.Serialize(new
        {
            lanCacheEnabled = true, domainsUpdatePeriodHours = 24,
            globalCacheAddresses = new[] { "192.168.1.10" },
            cacheAddresses = new Dictionary<string, string[]> { ["winner"] = new[] { "192.168.1.20" } }
        }));
        SetPrivateField("_lanCacheDomains", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXAMPLE.com"] = "winner"
        });
        SetPrivateField("_wildcardDomains", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deep.example.com"] = "loser"
        });
        var request = new DnsDatagram(1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { new DnsQuestionRecord("x.deep.example.com", DnsResourceRecordType.A, DnsClass.IN) });
        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Loopback, 1234),
            DnsTransportProtocol.Udp, true);
        Assert.That(response, Is.Not.Null);
        Assert.That(((DnsARecordData)response!.Answer.Single().RDATA).Address, Is.EqualTo(IPAddress.Parse("192.168.1.20")));
        Assert.That(response.Authority.Single().Name, Is.EqualTo("EXAMPLE.com").IgnoreCase);
        Assert.That(response.Additional.Single().RDATA.ToString(), Does.Contain("cache=winner, cache-domain=EXAMPLE.com"));
    }

    [Test]
    public void LookupMeasurements_CompareWarmedMatchers()
    {
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wildcard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 500; i++)
        {
            direct[$"service{i}.example.com"] = "direct";
            wildcard[$"service{i}.example.net"] = "wild";
        }

        static bool OldMatch(Dictionary<string, string> direct, Dictionary<string, string> wildcard,
            string domain, out string? zone, out string? target)
        {
            static string? Parent(string name)
            {
                var dot = name.IndexOf('.');
                return dot < 0 ? null : name[(dot + 1)..];
            }
            var suffix = domain.ToLower();
            while (suffix is not null)
            {
                if (direct.TryGetValue(suffix, out target)) { zone = suffix; return true; }
                suffix = Parent(suffix);
            }
            suffix = Parent(domain.ToLower());
            while (suffix is not null)
            {
                if (wildcard.TryGetValue(suffix, out target)) { zone = suffix; return true; }
                suffix = Parent(suffix);
            }
            zone = null;
            target = null;
            return false;
        }

        foreach (var query in new[] { "www.unrelated.org", "a.b.c.d.e.f.unrelated.org",
                     "x.service250.example.com", "x.service250.example.net" })
        {
            foreach (DomainMatcher matcher in new DomainMatcher[] { OldMatch, MatchDomain })
            {
                for (var i = 0; i < 100_000; i++) matcher(direct, wildcard, query, out _, out _);
                const int iterations = 500_000;
                var startBytes = GC.GetAllocatedBytesForCurrentThread();
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var hits = 0;
                for (var i = 0; i < iterations; i++)
                    if (matcher(direct, wildcard, query, out _, out _)) hits++;
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
                var bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
                TestContext.Out.WriteLine($"{matcher.Method.Name}: {query}: {elapsed.TotalMilliseconds:F2} ms, {bytes / (double)iterations:F2} B/op, hits={hits}");
                Assert.That(hits, Is.EqualTo(query.Contains("service250") ? iterations : 0));
                if (matcher == MatchDomain) Assert.That(bytes, Is.Zero);
            }
        }
    }

    [TestCase("example.net", true)]
    [TestCase("a.example.net", false)]
    public async Task ProcessRequestAsync_WildcardTargetGuard_ResolvesOnlyApex(string target, bool resolves)
    {
        _mockDnsServer.Setup(s => s.ResolveAsync(It.IsAny<DnsQuestionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DnsDatagram(0, true, DnsOpcode.StandardQuery, false, false, true, true, false, false,
                DnsResponseCode.NoError, Array.Empty<DnsQuestionRecord>()));
        await InitializeApp(JsonSerializer.Serialize(new
        {
            lanCacheEnabled = true, domainsUpdatePeriodHours = 24,
            globalCacheAddresses = new[] { target }
        }));
        SetPrivateField("_lanCacheDomains", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source.com"] = "steam"
        });
        SetPrivateField("_wildcardDomains", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["example.net"] = "steam"
        });
        var question = new DnsQuestionRecord(target, DnsResourceRecordType.A, DnsClass.IN);
        _mockDnsServer.Invocations.Clear();
        _mockDnsServer.Setup(s => s.ResolveAsync(It.IsAny<DnsQuestionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DnsDatagram(0, true, DnsOpcode.StandardQuery, false, false, true, true, false, false,
                DnsResponseCode.NoError, new[] { question }, new[]
                {
                    new DnsResourceRecord(target, DnsResourceRecordType.A, DnsClass.IN, 60,
                        new DnsARecordData(IPAddress.Parse("192.168.1.20")))
                }));
        var request = new DnsDatagram(1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { new DnsQuestionRecord("source.com", DnsResourceRecordType.A, DnsClass.IN) });
        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Loopback, 1234),
            DnsTransportProtocol.Udp, true);
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Answer, Has.Count.EqualTo(resolves ? 2 : 1));
        _mockDnsServer.Verify(s => s.ResolveAsync(It.Is<DnsQuestionRecord>(q => q.Name == target),
            It.IsAny<CancellationToken>()), resolves ? Times.Once() : Times.Never());
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
    [TestCase("_lanCacheDomains", "cache_domains.json")]
    [TestCase("_wildcardDomains", "wildcard_domains.json")]
    public async Task LoadCachedDomainsData_RebuildsCaseInsensitiveMapWithoutMutatingPublishedMap(string fieldName, string fileName)
    {
        await InitializeApp("{\"lanCacheEnabled\":true}");
        var field = typeof(App).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var original = (IReadOnlyDictionary<string, string>)field.GetValue(_app)!;
        var path = Path.Combine(_tempFolder, fileName);
        const string json = "{\"EXAMPLE.com\":\"first\",\"example.COM\":\"last\",\"Other.NET\":\"other\"}";
        await File.WriteAllTextAsync(path, json);

        var load = typeof(App).GetMethod("LoadCachedDomainsData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)load.Invoke(_app, null)!;

        var domains = field.GetValue(_app) as Dictionary<string, string>;
        Assert.That(domains, Is.Not.Null);
        Assert.That(domains!.Comparer, Is.EqualTo(StringComparer.OrdinalIgnoreCase));
        Assert.That(domains, Has.Count.EqualTo(2));
        Assert.That(domains["eXaMpLe.CoM"], Is.EqualTo("last"));
        Assert.That(domains.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue("OTHER.net".AsSpan(), out var target), Is.True);
        Assert.That(target, Is.EqualTo("other"));
        Assert.That(original, Is.Empty);
        Assert.That(domains, Is.Not.SameAs(original));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(json));

        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(domains));
        Assert.That(serialized.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(serialized.RootElement.EnumerateObject().Select(p => p.Value.GetString()),
            Is.EquivalentTo(new[] { "last", "other" }));
    }

    [Test]
    public void NewApp_DomainMapsSupportCaseInsensitiveAlternateLookup()
    {
        foreach (var fieldName in new[] { "_lanCacheDomains", "_wildcardDomains" })
        {
            var domains = (Dictionary<string, string>)typeof(App)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_app)!;
            Assert.That(domains.Comparer, Is.EqualTo(StringComparer.OrdinalIgnoreCase));
            Assert.That(domains.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey("example.com".AsSpan()), Is.False);
        }
    }

    [Test]
    public async Task UpdateLanCacheDomains_FromArchive_PublishesCaseInsensitiveMapsAndPersistsObjects()
    {
        var config = new
        {
            lanCacheEnabled = true,
            domainsDataUrl = "invalid://cache-domains",
            domainsDataPathPrefix = "test/",
            domainsUpdatePeriodHours = 24
        };
        await InitializeApp(JsonSerializer.Serialize(config));
        var originalDirect = typeof(App).GetField("_lanCacheDomains", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_app);
        var originalWildcard = typeof(App).GetField("_wildcardDomains", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_app);
        using (var archive = ZipFile.Open(Path.Combine(_tempFolder, "lancache-domains.zip"), ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("test/cache_domains.json").Open()))
            {
                await writer.WriteAsync("{\"cache_domains\":[{\"name\":\"first\",\"domain_files\":[\"first.txt\"]},{\"name\":\"last\",\"domain_files\":[\"last.txt\"]}]}");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("test/first.txt").Open()))
            {
                await writer.WriteAsync("EXAMPLE.com\n*.WILD.example.com\n");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("test/last.txt").Open()))
            {
                await writer.WriteAsync("example.COM\n*.wild.EXAMPLE.com\n");
            }
        }

        SetPrivateField("_domainsLastUpdated", DateTime.MinValue);
        var update = typeof(App).GetMethod("UpdateLanCacheDomains", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)update.Invoke(_app, null)!;

        foreach (var (fieldName, fileName, key, original) in new[]
                 {
                     ("_lanCacheDomains", "cache_domains.json", "example.com", originalDirect),
                     ("_wildcardDomains", "wildcard_domains.json", "wild.example.com", originalWildcard)
                 })
        {
            var domains = (Dictionary<string, string>)typeof(App)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_app)!;
            Assert.That(domains.Comparer, Is.EqualTo(StringComparer.OrdinalIgnoreCase));
            Assert.That(domains, Has.Count.EqualTo(1));
            Assert.That(domains.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key.AsSpan(), out var target), Is.True);
            Assert.That(target, Is.EqualTo("last"));
            Assert.That(domains, Is.Not.SameAs(original));
            Assert.That((IReadOnlyDictionary<string, string>)original!, Is.Empty);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tempFolder, fileName)));
            Assert.That(persisted.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(persisted.RootElement.EnumerateObject().Single().Value.GetString(), Is.EqualTo("last"));
        }
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
        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", domains);

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
        var wildcardDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "steampowered.com", "steam" } };
        SetPrivateField("_wildcardDomains", wildcardDomains);

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("anything.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234), DnsTransportProtocol.Udp, true);

        Assert.That(response, Is.Not.Null);
        Assert.That(response.Answer.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessRequestAsync_WithWildcardMatch_DoesNotMatchApexButMatchesSubdomains()
    {
        var config = new
        {
            lanCacheEnabled = true,
            operatingMode = "Authoritative",
            globalCacheAddresses = new[] { "192.168.1.10" }
        };
        await InitializeApp(JsonSerializer.Serialize(config));

        var wildcardDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "example.com", "steam" } };
        SetPrivateField("_wildcardDomains", wildcardDomains);

        async Task<DnsDatagram?> Process(string domain)
        {
            var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
                DnsResponseCode.NoError,
                new[] { new DnsQuestionRecord(domain, DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

            return await _app.ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234),
                DnsTransportProtocol.Udp, true);
        }

        Assert.That(await Process("example.com"), Is.Null);

        foreach (var domain in new[] { "a.example.com", "a.b.example.com" })
        {
            var response = await Process(domain);

            Assert.That(response, Is.Not.Null, domain);
            Assert.That(response!.Answer, Has.Count.EqualTo(1), domain);
            var aRecord = response.Answer[0].RDATA as DnsARecordData;
            Assert.That(aRecord, Is.Not.Null, domain);
            Assert.That(aRecord!.Address, Is.EqualTo(IPAddress.Parse("192.168.1.10")), domain);
        }
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

        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", domains);

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

        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", domains);

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

        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", domains);

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

        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "content1.steampowered.com", "steam" } };
        SetPrivateField("_lanCacheDomains", domains);

        var request = new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            new[] { new DnsQuestionRecord("content1.steampowered.com", DnsResourceRecordType.A, DnsClass.IN) }, null, null, null);

        var response = await ((IDnsRequestBlockingHandler)_app).ProcessRequestAsync(request, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234));

        Assert.That(response, Is.Not.Null);
        Assert.That(response.RCODE, Is.EqualTo(DnsResponseCode.NoError));
        Assert.That(response.Answer.Count, Is.EqualTo(1));
    }
}
