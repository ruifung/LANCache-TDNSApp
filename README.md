# LANCache-TDNSApp
[![build-dev](https://github.com/ruifung/LANCache-TDNSApp/actions/workflows/build-dev.yml/badge.svg)](https://github.com/ruifung/LANCache-TDNSApp/actions/workflows/build-dev.yml)
[![release](https://github.com/ruifung/LANCache-TDNSApp/actions/workflows/release.yml/badge.svg)](https://github.com/ruifung/LANCache-TDNSApp/actions/workflows/release.yml)
[![GitHub Release](https://img.shields.io/github/v/release/ruifung/LANCache-TDNSApp?sort=semver&display_name=release&label=Release)](https://github.com/ruifung/LANCache-TDNSApp/releases/latest)
[![Ask DeepWiki](https://img.shields.io/badge/DeepWiki-ruifung%2FLANCache--TDNSApp-blue.svg?logo=data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACwAAAAyCAYAAAAnWDnqAAAAAXNSR0IArs4c6QAAA05JREFUaEPtmUtyEzEQhtWTQyQLHNak2AB7ZnyXZMEjXMGeK/AIi+QuHrMnbChYY7MIh8g01fJoopFb0uhhEqqcbWTp06/uv1saEDv4O3n3dV60RfP947Mm9/SQc0ICFQgzfc4CYZoTPAswgSJCCUJUnAAoRHOAUOcATwbmVLWdGoH//PB8mnKqScAhsD0kYP3j/Yt5LPQe2KvcXmGvRHcDnpxfL2zOYJ1mFwrryWTz0advv1Ut4CJgf5uhDuDj5eUcAUoahrdY/56ebRWeraTjMt/00Sh3UDtjgHtQNHwcRGOC98BJEAEymycmYcWwOprTgcB6VZ5JK5TAJ+fXGLBm3FDAmn6oPPjR4rKCAoJCal2eAiQp2x0vxTPB3ALO2CRkwmDy5WohzBDwSEFKRwPbknEggCPB/imwrycgxX2NzoMCHhPkDwqYMr9tRcP5qNrMZHkVnOjRMWwLCcr8ohBVb1OMjxLwGCvjTikrsBOiA6fNyCrm8V1rP93iVPpwaE+gO0SsWmPiXB+jikdf6SizrT5qKasx5j8ABbHpFTx+vFXp9EnYQmLx02h1QTTrl6eDqxLnGjporxl3NL3agEvXdT0WmEost648sQOYAeJS9Q7bfUVoMGnjo4AZdUMQku50McDcMWcBPvr0SzbTAFDfvJqwLzgxwATnCgnp4wDl6Aa+Ax283gghmj+vj7feE2KBBRMW3FzOpLOADl0Isb5587h/U4gGvkt5v60Z1VLG8BhYjbzRwyQZemwAd6cCR5/XFWLYZRIMpX39AR0tjaGGiGzLVyhse5C9RKC6ai42ppWPKiBagOvaYk8lO7DajerabOZP46Lby5wKjw1HCRx7p9sVMOWGzb/vA1hwiWc6jm3MvQDTogQkiqIhJV0nBQBTU+3okKCFDy9WwferkHjtxib7t3xIUQtHxnIwtx4mpg26/HfwVNVDb4oI9RHmx5WGelRVlrtiw43zboCLaxv46AZeB3IlTkwouebTr1y2NjSpHz68WNFjHvupy3q8TFn3Hos2IAk4Ju5dCo8B3wP7VPr/FGaKiG+T+v+TQqIrOqMTL1VdWV1DdmcbO8KXBz6esmYWYKPwDL5b5FA1a0hwapHiom0r/cKaoqr+27/XcrS5UwSMbQAAAABJRU5ErkJggg==)](https://deepwiki.com/ruifung/LANCache-TDNSApp)

Technitium DNS App for LANCache.NET

This DNS App, will use a specified set of domains to cache. And intercept requests for those domains with configured lancache addresses.<br/>
This implements the functionality of [Lancache-DNS](https://lancache.net/docs/containers/dns/) and is intended to be used with [LANCache Monolithic](https://lancache.net/docs/containers/monolithic/).

Cache targets may be hostnames as well as IP addresses. When a hostname is used, it is resolved internally by Technitium DNS, so the target can be provided by another T-DNS APP record—for example, the [Split Horizon](https://github.com/TechnitiumSoftware/DnsServer/tree/master/Apps/SplitHorizonApp) or Failover app. This lets those apps select or fail over between cache addresses when resolving the target.

## Requirements
1. Technitium DNS server
2. LANCache Monolithic instance(s) (Or other compatible implementation.)
3. The cache server should ideally be [accessible using a address in the ULA/RFC1918 IP address range](https://lancache.net/docs/common-issues/#non-rfc1918-ip-ranges). 
## Installation

1. Download the ZIP file from [Github Releases](https://github.com/ruifung/LANCache-TDNSApp/releases/latest)
2. Go to 'Apps' in Technitium DNS
3. Click the 'Install' button
4. Specify a name (This is just for your reference)
5. Select the ZIP file you downloaded
6. Click the 'Install' button
7. Done

## Updating

1. Download the ZIP file from [Github Releases](https://github.com/ruifung/LANCache-TDNSApp/releases/latest)
2. Go to 'Apps' in Technitium DNS
3. Find the installed app in the list
4. Click the 'Update' button
5. Select the ZIP file you downloaded
6. Click the 'Update' button
7. Done

## Configuration
| Property                        | Description                                                                                                                                                                                                                                                                                      | Default                                                                 |
|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------|
| `lanCacheEnabled`               | Enables LANCache domain interception.                                                                                                                                                                                                                                                            | `false`                                                                 |
| `appPreference`                 | Priority when multiple DNS Apps overlap; lower values are evaluated first.                                                                                                                                                                                                                       | `50`                                                                    |
| `operatingMode`                 | T-DNS App mode in which LANCache handling runs. Valid values: `Blocking` or `Authoritative`.                                                                                                                                                                                                     | `Authoritative`                                                         |
| `enableDebugLogging`            | Enables debug log messages.                                                                                                                                                                                                                                                                      | `false`                                                                 |
| `domainsDataUrl`                | URL for the LANCache domains ZIP. It must contain a `cache_domains.json` file. See [cache-domains](https://github.com/uklans/cache-domains).                                                                                                                                                     | `https://github.com/uklans/cache-domains/archive/refs/heads/master.zip` |
| `domainsDataPathPrefix`         | Path prefix to `cache_domains.json` inside the ZIP; GitHub archive downloads put the repository contents in a subfolder.                                                                                                                                                                         | `cache-domains-master/`                                                 |
| `domainsUpdatePeriodHours`      | How often to check for updates to the cached domains list, in hours.                                                                                                                                                                                                                             | `24`                                                                    |
| `ignoreClientAddresses`         | Client IPv4/IPv6 addresses, CIDR subnets, or FQDNs to skip. FQDNs are resolved to IPv4/IPv6 addresses at app initialization. Configured cache target addresses are automatically included. Add `0.0.0.0` if internal DNS App lookups should be ignored, since those may use that source address. | `[]`                                                                    |
| `globalCacheAddresses`          | Cache targets used when no cache-type override is configured. Each target may be a hostname, IPv4 address, or IPv6 address.                                                                                                                                                                      | `[]`*                                                                   |
| `cacheAddresses`                | Per-cache-type target overrides. Cache types come from `cache_domains.json`. Use `{}` to use `globalCacheAddresses` for all cache types. Each target may be a hostname or IP address.                                                                                                            | `{}`*                                                                   |
| `enabledCaches`                 | Allowlist of cache types. When non-empty, only listed types are enabled; use `[]` to disable the allowlist.                                                                                                                                                                                      | `[]`                                                                    |
| `disabledCaches`                | Denylist of cache types. Listed types are disabled; use `[]` for no exclusions.                                                                                                                                                                                                                  | `[]`                                                                    |
| `recordTtl`                     | TTL, in seconds, used for records returned for cached domains.                                                                                                                                                                                                                                   | `3600`                                                                  |
| `legacyUpstreamBehavior`        | For any cached domain with no cache answer, allow normal DNS processing to continue.                                                                                                                                                                                                             | `false`                                                                 |
| `fallbackOnCnameTargetNoAnswer` | For hostname cache targets, allow normal DNS processing to continue when target resolution does not produce an answer matching the requested record type. When `false`, retain the CNAME even if no matching target record was resolved.                                                         | `false`                                                                 |

\* The generated default configuration uses `lancache.example.com` as a placeholder in `globalCacheAddresses`, and for the `steam` and `windowsupdates` entries in `cacheAddresses`. Replace these example targets with your cache server(s). When a property is omitted from a supplied configuration, its type default is used (empty lists/maps, `false`, or the property default shown above).

### Default configuration
```json
{
  "lanCacheEnabled": false,
  "appPreference": 50,
  "operatingMode": "Authoritative",
  "enableDebugLogging": false,
  "domainsDataUrl": "https://github.com/uklans/cache-domains/archive/refs/heads/master.zip",
  "domainsDataPathPrefix": "cache-domains-master/",
  "domainsUpdatePeriodHours": 24,
  "ignoreClientAddresses": [],
  "globalCacheAddresses": [
    "lancache.example.com"
  ],
  "cacheAddresses": {
    "steam": [
      "lancache.example.com"
    ],
    "windowsupdates": [
      "lancache.example.com"
    ]
  },
  "enabledCaches": [],
  "disabledCaches": [],
  "recordTtl": 3600,
  "legacyUpstreamBehavior": false,
  "fallbackOnCnameTargetNoAnswer": false
}
```

### Example configuration (All caches enabled)
```json
{
  "lanCacheEnabled": true,
  "enableDebugLogging": false,
  "domainsDataUrl": "https://github.com/uklans/cache-domains/archive/refs/heads/master.zip",
  "domainsDataPathPrefix": "cache-domains-master/",
  "domainsUpdatePeriodHours": 24,
  "ignoreClientAddresses": [],
  "globalCacheAddresses": [
    "lancache.example.home.internal"
  ],
  "cacheAddresses": {},
  "enabledCaches": [],
  "disabledCaches": [],
  "recordTtl": 3600,
  "legacyUpstreamBehavior": false,
  "fallbackOnCnameTargetNoAnswer": false
}
```

## Troubleshooting LANCache issues
Please have a look at https://lancache.net/docs/common-issues

Note: Issues related to lancache-dns are not relevant as this replaces that.

### IPv6 Support
While the official lancache-dns does not support IPv6, LANCache-TDNSApp does.

Ensure your lancache-monolithic (Or other caching implementation) is IPv6 enabled and it should just work.

## AI Usage Disclosure
AI tooling has been used to help in the maintenance of documentation and code.
AI-generated code has been produced under supervision and reviewed appropriately.