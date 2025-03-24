# LANCache-TDNSApp

Technitium DNS App for LANCache.NET

This DNS App, will use a specified set of domains to cache. And intercept requests for those domains with configured lancache addresses.<br/>
This implements the functionality of [Lancache-DNS](https://lancache.net/docs/containers/dns/) and is intended to be used with [LANCache Monolithic](https://lancache.net/docs/containers/monolithic/).

## Requirements
1. Technitium DNS server
2. LANCache Monolithic instance(s) (Or other compatible implementation.)

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
| Property                 | Description                                                                                                                                                                                                                                 | Default                                                               |
|--------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------|
| lanCacheEnabled          | Is LANCache functionality enabled.                                                                                                                                                                                                          | false                                                                 |
| enableDebugLogging       | Enable debug log messages                                                                                                                                                                                                                   | false                                                                 |
| domainsDataUrl           | The URL to which to fetch LANCache domains data from.<br/>Must be a zip file containing a cache_domains.json file. <br/> See https://github.com/uklans/cache-domains                                                                        | https://github.com/uklans/cache-domains/archive/refs/heads/master.zip |
| domainsDataPathPrefix    | The path prefix to cache_domains.json inside the ZIP file. Useful since GitHub repository archive downloads have the contents in a subfolder.                                                                                               | cache-domains-master/                                                 |
| domainsUpdatePeriodHours | How often to check for updates to the cached domains list.                                                                                                                                                                                  | 24                                                                    |
| globalCacheAddresses     | The lancache instances to use where a specific cache addresses have not been specified.<br/> May be either a valid domain, IPv4 address or IPv6 address.                                                                                    | ["lancache.example.com"]                                              |
| cacheAddresses           | Used to override the cache addresses for specific cache types.<br/> Consult the cache_domains.json file in file specified in domainsDataUrl for cache types.<br/> Set to empty object {} if all caches should use the globalCacheAddresses. | { "steam": ["lancache.example.com"] }                                 |
| enabledCaches            | Cache type enable list. Used if set and non-empty.<br/> If non-empty, cache types MUST be specified here to be used.<br/> Set to empty list [] to disable.                                                                                  | ["steam"]                                                             |
| disabledCaches           | Cache type disable list. Used if set and non-empty.<br/> If non-empty, cache types MUST NOT be specified here to be used.<br/> Set to empty list [] to disable.                                                                             | ["wsus"]                                                              |

### Default configuration
```json
{
  "lanCacheEnabled": false,
  "enableDebugLogging": false,
  "domainsDataUrl": "https://github.com/uklans/cache-domains/archive/refs/heads/master.zip",
  "domainsDataPathPrefix": "cache-domains-master/",
  "domainsUpdatePeriodHours": 24,
  "globalCacheAddresses": [
    "lancache.example.com"
  ],
  "cacheAddresses": {
    "steam": [
      "lancache.example.com"
    ]
  },
  "enabledCaches": [
    "steam"
  ],
  "disabledCaches": [
    "wsus"
  ]
}
```