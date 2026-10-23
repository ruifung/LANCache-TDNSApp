using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCache;

public enum OperatingMode
{
    Blocking,
    Authoritative
}

public partial class App
{
    private async Task<DnsDatagram?> HandleLancacheRequest(DnsDatagram request, IPEndPoint remoteEp, OperatingMode mode)
    {
        if (!_config.LanCacheEnabled || mode != _config.OperatingMode ||
            _ignoredClientNetworkAddresses.Any(netAddr => netAddr.Contains(remoteEp.Address)))
        {
            return null;
        }

        var question = request.Question[0];
        var domains = _domainSnapshot;
        if (!TryFindCacheDomain(domains.DirectDomains, domains.WildcardDomains, question.Name, out var foundZone, out var cacheTarget) || cacheTarget == null)
        {
            return null;
        }

        // Skip processing if the enabled cache whitelist is set, and the cache isn't enabled.
        if (_config.EnabledCaches.Count > 0 && !_config.EnabledCaches.Contains(cacheTarget))
        {
            return null;
        }

        // Skip processing if the disabled cache blacklist is set, and the cache is disabled.
        if (_config.DisabledCaches.Count > 0 && _config.DisabledCaches.Contains(cacheTarget))
        {
            return null;
        }

        WriteDebugLog("Resolving cache addresses for cache: " + cacheTarget);
        var hasOverride = _config.CacheAddresses.TryGetValue(cacheTarget, out var cacheAddresses);
        if (!hasOverride || cacheAddresses == null)
        {
            cacheAddresses = _config.GlobalCacheAddresses;
        }

        // Skip processing if there are no cache addresses specified for the cache. (Effectively disabled)
        if (cacheAddresses.Count == 0)
        {
            _dnsServer.WriteLog("Skipping cache as they are no cache addresses specified for cache: " + cacheTarget);
            return null;
        }

        WriteDebugLog("Resolved cache addresses: " + string.Join(", ", cacheAddresses));
        var answers = new List<DnsResourceRecord>();
        var additional = new List<DnsResourceRecord>();
        var domainZone = foundZone ?? question.Name;
        var authority = new[]
        {
            new DnsResourceRecord(domainZone, DnsResourceRecordType.SOA, question.Class, _config.RecordTtl, _soaRecord)
        };
        
        additional.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.TXT, question.Class, 60,
            new DnsTXTRecordData($"source=lancache-app; cache={cacheTarget}, cache-domain={domainZone}")));

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (question.Type)
        {
            case DnsResourceRecordType.NS:
                answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.NS, question.Class, _config.RecordTtl,
                    _nsRecord));
                break;
            case DnsResourceRecordType.SOA:
                answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.SOA, question.Class,
                    _config.RecordTtl, _soaRecord));
                break;
            default:
            {
                foreach (var cacheAddress in cacheAddresses.Where(t => t != DUMMY_LANCACHE_ADDRESS))
                {
                    var isIpAddress = IPAddress.TryParse(cacheAddress, out var ipAddress);
                    if (isIpAddress)
                    {
                        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                        switch (ipAddress?.AddressFamily)
                        {
                            case AddressFamily.InterNetwork:
                                if (question.Type == DnsResourceRecordType.A)
                                {
                                    WriteDebugLog("Creating A record");
                                    answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.A,
                                        question.Class, _config.RecordTtl, new DnsARecordData(ipAddress)));
                                }

                                break;
                            case AddressFamily.InterNetworkV6:
                                if (question.Type == DnsResourceRecordType.AAAA)
                                {
                                    WriteDebugLog("Creating AAAA record");
                                    answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.AAAA,
                                        question.Class, _config.RecordTtl, new DnsAAAARecordData(ipAddress)));
                                }

                                break;
                            default:
                                _dnsServer.WriteLog("Found non v4/v6 IP address somehow: " + cacheAddress);
                                break;
                        }
                    }
                    else
                    {
                        if (cacheAddress == question.Name)
                            _dnsServer.WriteLog($"Attempted to cache {cacheAddress} using itself!");
                        else
                            try
                            {
                                WriteDebugLog("Creating CNAME record");
                                answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.CNAME,
                                    question.Class,
                                    _config.RecordTtl,
                                    new DnsCNAMERecordData(cacheAddress)));
                                var newQuestion = new DnsQuestionRecord(cacheAddress, question.Type, question.Class);
                            
                                var cacheAddressIsInCachedDomains = domains.CachedTargets.Contains(cacheAddress);
                                if (!cacheAddressIsInCachedDomains)
                                {
                                    WriteDebugLog($"Querying for {question.Type} records for cache server {cacheAddress}");
                                    var targetRequest = new DnsDatagram(request.Identifier, false, request.OPCODE,
                                        false, false, request.RecursionDesired, false, false, false,
                                        DnsResponseCode.NoError, [newQuestion]);
                                    var newResponse = await _dnsServer.DirectQueryAsync(targetRequest, remoteEp);
                                    if (newResponse.RCODE is DnsResponseCode.NoError)
                                        answers.AddRange(newResponse.Answer);
                                    else
                                        _dnsServer.WriteLog(
                                            $"Error querying cache target {cacheAddress} for QTYPE {question.Type} with RCODE {newResponse.RCODE}");
                                }
                                else
                                {
                                    WriteDebugLog($"Cache address is itself, present in cached domains, skipping CNAME resolution.");
                                    additional.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.TXT, question.Class, 60,
                                        new DnsTXTRecordData("source=lancache-app; warning=cache-address-is-cached-domain")));
                                }
                            }
                            catch (DnsClientException)
                            {
                                _dnsServer.WriteLog("Invalid CNAME target: " + cacheAddress);
                            }
                    }
                }

                break;
            }
        }

        var hasMatchingTypeAnswer = answers.Any(answer => answer.Type == question.Type);
        var fallbackEnabled = _config.FallbackOnCnameTargetNoAnswer;
        // If fallback is enabled, and the first answer is a CNAME, and there is no matching type answer.
        // Return null so that T-DNS will handle the query normally.
        if (fallbackEnabled && answers.Count > 0 && answers[0].Type == DnsResourceRecordType.CNAME && !hasMatchingTypeAnswer)
        {
            return null;
        }
        
        // Happy path, return the response if it has answers and the first answer is not a CNAME
        if (answers.Count > 0)
        {
            WriteDebugLog($"Returning DNSDatagram with {answers.Count} answers");
            return new DnsDatagram(request.Identifier, true, request.OPCODE, true, false,
                request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, answers,
                authority, additional);
        }
        
        // If Legacy behavior is enabled, return null so that T-DNS will handle the query normally.
        if (_config.LegacyUpstreamBehavior)
        {
            if (question.Type is DnsResourceRecordType.A or DnsResourceRecordType.AAAA)
                _dnsServer.WriteLog($"No valid targets found for cache: {cacheTarget}, skipping processing");

            return null;
        }

        // Return NXDomain for intercepted cache domains.
        // This aligns with the official lancache-dns as that configures a zone per cached domain, which would effectively result in this.
        WriteDebugLog("Returning DNSDatagram with NXDOMAIN response");
        return new DnsDatagram(request.Identifier, true, request.OPCODE, true, false,
            request.RecursionDesired, true, false, false, DnsResponseCode.NxDomain, request.Question, null, authority);
    }
}