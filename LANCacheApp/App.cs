using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.ApplicationCommon;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;
using TechnitiumLibrary.Net.Http.Client;

namespace LanCache
{
    public partial class App : IDnsApplication, IDnsApplicationPreference
    {
        #region variables

        // ReSharper disable once InconsistentNaming
        private const string LANCACHE_DOMAINS_DATA_URL =
            "https://github.com/uklans/cache-domains/archive/refs/heads/master.zip";

        // ReSharper disable once InconsistentNaming
        private const string LANCACHE_DOMAINS_DEFAULT_ZIP_PREFIX = "cache-domains-master/";

        // ReSharper disable once InconsistentNaming
        private const string DUMMY_LANCACHE_ADDRESS = "lancache.example.com";
        private DateTime _domainsLastUpdated;
        private Timer? _domainsUpdateTimer;
        private IDnsServer _dnsServer = null!;
        private AppConfig _config = null!;
        private ReadOnlyDictionary<string, string> _lanCacheDomains = new(new Dictionary<string, string>());
        private ReadOnlyDictionary<string, string> _wildcardDomains = new(new Dictionary<string, string>());
        private DnsSOARecordData _soaRecord = null!;
        private DnsNSRecordData _nsRecord = null!;
        private string _cacheDomainsCacheFile = null!;
        private string _wildcardDomainsCacheFile = null!;
#pragma warning disable CA1859
        private IReadOnlySet<NetworkAddress> _ignoredClientNetworkAddresses = new HashSet<NetworkAddress>();
#pragma warning restore CA1859

        #endregion

        #region private

        private void WriteDebugLog(string message)
        {
            if (_config.EnableDebugLogging)
            {
                _dnsServer.WriteLog(message);
            }
        }

        private async Task LoadCachedDomainsData()
        {
            if (File.Exists(_cacheDomainsCacheFile))
            {
                var cacheFile = File.OpenRead(_cacheDomainsCacheFile);
                var parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(cacheFile);
                if (parsed != null)
                {
                    _lanCacheDomains = new ReadOnlyDictionary<string, string>(parsed);
                    _dnsServer.WriteLog("Loaded cached direct domains.");
                }
            }

            if (File.Exists(_wildcardDomainsCacheFile))
            {
                var cacheFile = File.OpenRead(_wildcardDomainsCacheFile);
                var parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(cacheFile);
                if (parsed != null)
                {
                    _wildcardDomains = new ReadOnlyDictionary<string, string>(parsed);
                    _dnsServer.WriteLog("Loaded cached wildcard domains.");
                }
            }
        }

        private static readonly JsonSerializerOptions CacheDomainsIndexSerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        [SuppressMessage("ReSharper", "InvertIf")]
        private async Task UpdateLanCacheDomains()
        {
            if (DateTime.Now.Subtract(_domainsLastUpdated) < TimeSpan.FromHours(_config.DomainsUpdatePeriodHours))
            {
                _dnsServer.WriteLog("Update of cache domains skipped due to set interval.");
                return;
            }

            _dnsServer.WriteLog("Updating cache domains.");
            var handler = new HttpClientNetworkHandler();
            handler.Proxy = _dnsServer.Proxy;
            handler.InnerHandler.AutomaticDecompression = DecompressionMethods.All;
            handler.NetworkType = _dnsServer.IPv6Mode switch
            {
                IPv6Mode.Disabled => HttpClientNetworkType.IPv4Only,
                IPv6Mode.Enabled => HttpClientNetworkType.Default,
                IPv6Mode.Preferred => HttpClientNetworkType.PreferIPv6,
                _ => HttpClientNetworkType.Default
            };
            handler.DnsClient = _dnsServer;
            handler.EnableDANE = false;
            using var hc = new HttpClient(handler);
            var domainsZipFile = Path.Combine(_dnsServer.ApplicationFolder, "lancache-domains.zip");
            try
            {
                var respStream = await hc.GetStreamAsync(_config.DomainsDataUrl);
                await using (var fileStream = new FileStream($"{domainsZipFile}.download", FileMode.Create))
                {
                    await respStream.CopyToAsync(fileStream);
                }

                File.Move($"{domainsZipFile}.download", domainsZipFile, true);
            }
            catch (Exception ex)
            {
                _dnsServer.WriteLog("Failed to download domains zip file. Reusing previous.");
                _dnsServer.WriteLog(ex.ToString());
            }

            if (!File.Exists(domainsZipFile))
            {
                _dnsServer.WriteLog("No existing domains zip file found. Attempting load of cached data.");
                await LoadCachedDomainsData();
                return;
            }


            await using var archive = await ZipFile.OpenReadAsync(domainsZipFile);
            var domainsJsonEntry = archive.GetEntry($"{_config.DomainsDataPathPrefix}cache_domains.json");
            if (domainsJsonEntry == null)
            {
                _dnsServer.WriteLog("cache_domains.json not found in domains zip file.");
                _dnsServer.WriteLog("Entries in domains zip file:");
                foreach (var zipArchiveEntry in archive.Entries)
                {
                    _dnsServer.WriteLog("- " + zipArchiveEntry.FullName);
                }

                await LoadCachedDomainsData();
                return;
            }

            await using var entryStream = await domainsJsonEntry.OpenAsync();
            var cacheDomains = await JsonSerializer.DeserializeAsync<CacheDomainsIndex>(entryStream, CacheDomainsIndexSerializerOptions);
            if (cacheDomains == null)
            {
                _dnsServer.WriteLog("Fail to parse cache_domains.json");
                await LoadCachedDomainsData();
                return;
            }

            var newCacheDomains = new Dictionary<string, string>();
            var wildcardDomains = new Dictionary<string, string>();
            foreach (var entry in cacheDomains.CacheDomains.Where(e => e.DomainFiles.Count > 0))
            {
                foreach (var zipEntry in entry.DomainFiles
                             .Select(f => archive.GetEntry($"{_config.DomainsDataPathPrefix}{f}")).Where(s => s != null)
                             .Select(s => s!))
                {
                    await using var stream = await zipEntry.OpenAsync();
                    using var reader = new StreamReader(stream);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (line.StartsWith("*."))
                        {
                            wildcardDomains[line[2..]] = entry.Name;
                        }
                        else
                        {
                            newCacheDomains[line] = entry.Name;
                        }
                    }
                }
            }

            _lanCacheDomains = new ReadOnlyDictionary<string, string>(newCacheDomains);
            _wildcardDomains = new ReadOnlyDictionary<string, string>(wildcardDomains);
            _domainsLastUpdated = DateTime.Now;
            _dnsServer.WriteLog("Updated cache domains.");

            await using var cacheDomainsFile =
                new FileStream(_cacheDomainsCacheFile, FileMode.Create);
            await JsonSerializer.SerializeAsync(cacheDomainsFile, _lanCacheDomains);
            await using var wildcardDomainsFile =
                new FileStream(_wildcardDomainsCacheFile, FileMode.Create);
            await JsonSerializer.SerializeAsync(wildcardDomainsFile, _wildcardDomains);
        }

        private static readonly JsonSerializerOptions ConfigJsonSerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }; 

        private static async Task<AppConfig> LoadOrInitializeConfig(IDnsServer dnsServer, string? config)
        {
            try
            {
                if (config is not null)
                    return JsonSerializer.Deserialize<AppConfig>(config, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }) ?? throw new InvalidOperationException();
                dnsServer.WriteLog("No config file found. Using default config.");
                throw new InvalidOperationException();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                dnsServer.WriteLog("Invalid config. Writing default config file.");
                dnsServer.WriteLog(ex.ToString());
                dnsServer.WriteLog($"Config: {config}");
                var appConfig = new AppConfig
                {
                    LanCacheEnabled = false,
                    DomainsDataUrl = LANCACHE_DOMAINS_DATA_URL,
                    DomainsDataPathPrefix = LANCACHE_DOMAINS_DEFAULT_ZIP_PREFIX,
                    DomainsUpdatePeriodHours = 24,
                    GlobalCacheAddresses = [DUMMY_LANCACHE_ADDRESS],
                    CacheAddresses = new Dictionary<string, List<string>>
                    {
                        { "steam", [DUMMY_LANCACHE_ADDRESS] },
                        { "windowsupdates", [DUMMY_LANCACHE_ADDRESS] }
                    }
                };
                var configFilePath = Path.Combine(dnsServer.ApplicationFolder, "dnsApp.config");
                await using var configFileStream = new FileStream(configFilePath, FileMode.Create);
                await JsonSerializer.SerializeAsync(configFileStream, appConfig,ConfigJsonSerializerOptions);
                return appConfig;
            }
        }

        private async void DomainsUpdateTimerCallback(object? state)
        {
            try
            {
                await UpdateLanCacheDomains();
            }
            catch (Exception e)
            {
                _dnsServer.WriteLog("Failed to update cache domains.");
                _dnsServer.WriteLog(e);
            }
        }

        private async Task PrepareIgnoredClientsSet()
        {
            var addressSet = _config.GlobalCacheAddresses
                .Concat(_config.CacheAddresses.Values.SelectMany(cacheAddressesValue => cacheAddressesValue))
                .Concat(_config.IgnoreClientAddresses)
                .Where(addr => addr != DUMMY_LANCACHE_ADDRESS)
                .ToHashSet();

            var addrSet = new HashSet<NetworkAddress>();
            foreach (var addr in addressSet)
            {
                var isNetAddr = NetworkAddress.TryParse(addr, out var networkAddress);
                if (isNetAddr &&
                    networkAddress?.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    addrSet.Add(networkAddress);
                }
                else
                {
                    try
                    {
                        var v4Result =
                            await _dnsServer.ResolveAsync(new DnsQuestionRecord(addr, DnsResourceRecordType.A,
                                DnsClass.IN));
                        var v6Result =
                            await _dnsServer.ResolveAsync(new DnsQuestionRecord(addr, DnsResourceRecordType.AAAA,
                                DnsClass.IN));
                        foreach (var dnsResourceRecord in v4Result.Answer.Where(ans =>
                                     ans.Type is DnsResourceRecordType.A))
                        {
                            var ip = ((DnsARecordData)dnsResourceRecord.RDATA).Address;
                            addrSet.Add(new NetworkAddress(ip, 32));
                        }

                        foreach (var dnsResourceRecord in v6Result.Answer.Where(ans =>
                                     ans.Type is DnsResourceRecordType.AAAA))
                        {
                            var ip = ((DnsAAAARecordData)dnsResourceRecord.RDATA).Address;
                            addrSet.Add(new NetworkAddress(ip, 128));
                        }
                    }
                    catch (DnsClientException)
                    {
                        _dnsServer.WriteLog($"Unable to resolve target domain: {addr}");
                    }
                }
            }

            _ignoredClientNetworkAddresses = addrSet;
        }

        #endregion

        #region public

        public byte Preference => _config.AppPreference;

        public string Description => "DNS App that implements LanCache.NET DNS functionality.";

        public async Task InitializeAsync(IDnsServer dnsServer, string? config)
        {
            dnsServer.WriteLog("Initializing");
            _dnsServer = dnsServer;
            _cacheDomainsCacheFile = Path.Combine(_dnsServer.ApplicationFolder, "cache_domains.json");
            _wildcardDomainsCacheFile = Path.Combine(_dnsServer.ApplicationFolder, "wildcard_domains.json");
            _soaRecord = new DnsSOARecordData(_dnsServer.ServerDomain, "hostadmin@" + _dnsServer.ServerDomain, 1, 14400,
                3600, 604800, 60);
            _nsRecord = new DnsNSRecordData(_dnsServer.ServerDomain);
            _config = await LoadOrInitializeConfig(dnsServer, config);
            WriteDebugLog("Operating Mode: " + _config.OperatingMode);
            WriteDebugLog("Global Cache Addresses: " + string.Join(", ", _config.GlobalCacheAddresses));
            foreach (var cacheOverride in _config.CacheAddresses)
            {
                WriteDebugLog($"{cacheOverride.Key} Cache Addresses: " + string.Join(", ", cacheOverride.Value));
            }

            await PrepareIgnoredClientsSet();
            _domainsUpdateTimer = new Timer(DomainsUpdateTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
            _domainsUpdateTimer.Change(TimeSpan.FromHours(_config.DomainsUpdatePeriodHours),
                TimeSpan.FromHours(_config.DomainsUpdatePeriodHours));
            await UpdateLanCacheDomains();
        }

        public Task<bool> IsAllowedAsync(DnsDatagram request, IPEndPoint remoteEp)
        {
            return Task.FromResult(false);
        }

        private static string? GetParentZone(string domain)
        {
            var i = domain.IndexOf('.');
            //dont return root zone
            return i > -1 ? domain[(i + 1)..] : null;
        }

        private static bool IsZoneFound(IReadOnlyDictionary<string, string> domains, string domain)
        {
            return IsZoneFound(domains, domain, out _, out _);
        }
        
        private static bool IsZoneFound(IReadOnlyDictionary<string, string> domains, string domain,
            out string? foundZone, out string? cacheTarget)
        {
            var currentDomain = domain.ToLower();
            do
            {
                if (domains.TryGetValue(currentDomain, out var value))
                {
                    foundZone = currentDomain;
                    cacheTarget = value;
                    return true;
                }

                currentDomain = GetParentZone(currentDomain);
            } while (currentDomain is not null);

            foundZone = null;
            cacheTarget = null;
            return false;
        }

        public void Dispose()
        {
            _domainsUpdateTimer?.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    internal class AppConfig
    {
        public required bool LanCacheEnabled { get; init; }
        public bool EnableDebugLogging { get; init; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public OperatingMode OperatingMode { get; init; } = OperatingMode.Authoritative;

        public string DomainsDataUrl { get; init; } = "";
        public string DomainsDataPathPrefix { get; init; } = "";
        public int DomainsUpdatePeriodHours { get; init; }

        public List<string> IgnoreClientAddresses { get; init; } = [];
        public List<string> GlobalCacheAddresses { get; init; } = [];
        public List<string> EnabledCaches { get; init; } = [];
        public List<string> DisabledCaches { get; init; } = [];
        public Dictionary<string, List<string>> CacheAddresses { get; init; } = new();

        public byte AppPreference { get; init; } = 50;
        public uint RecordTtl { get; init; } = 3600;

        public bool LegacyUpstreamBehavior { get; init; }
    }

    [SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
    internal class CacheDomainsIndex
    {
        [JsonPropertyName("cache_domains")] public List<CacheDomainsEntry> CacheDomains { get; init; } = [];
    }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
    internal class CacheDomainsEntry
    {
        public required string Name { get; set; }
        public string? Description { get; set; }
        [JsonPropertyName("domain_files")] public List<string> DomainFiles { get; set; } = [];
        public string? Notes { get; set; }
        [JsonPropertyName("mixed_content")] public bool MixedContent { get; set; }
    }
}