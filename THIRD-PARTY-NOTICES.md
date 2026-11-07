# Third-party software bundled with Rhapsody Server

Rhapsody Server itself is released under the MIT licence (see `LICENSE`). The installer also redistributes the
unmodified programs below. Each is a separate program with its own licence; they are run as separate processes and are
not combined with Rhapsody Server's code. The full licence text for each program is inside its own archive
(look for `LICENSE`, `license.txt` or `COPYING` after the component is unpacked under
`%LOCALAPPDATA%\RhapsodyServer`), and at the links below.

| Component | Bundled version | Licence | Source / licence text |
|---|---|---|---|
| PHP | 8.5.x (NTS x64), other series downloaded on request | PHP License v3.01 | https://www.php.net/license/ , source: https://github.com/php/php-src |
| Apache HTTP Server (Apache Lounge build) | 2.4.x | Apache License 2.0 | https://www.apache.org/licenses/LICENSE-2.0 , build: https://www.apachelounge.com/ |
| nginx | 1.30.x | BSD 2-Clause | https://nginx.org/LICENSE , source: https://github.com/nginx/nginx |
| MariaDB Server | 11.8.x | **GNU GPL v2** | https://mariadb.com/kb/en/mariadb-licenses/ , source: https://github.com/MariaDB/server |
| phpMyAdmin | 5.2.x | **GNU GPL v2 or later** | https://www.phpmyadmin.net/license/ , source: https://github.com/phpmyadmin/phpmyadmin |
| Redis for Windows (tporadowski) | 5.0.14.1 | BSD 3-Clause | https://github.com/tporadowski/redis , upstream: https://redis.io/ |
| Mailpit | latest at build time | MIT | https://github.com/axllent/mailpit |
| mkcert | 1.4.4 | BSD 3-Clause | https://github.com/FiloSottile/mkcert |
| Composer | stable at build time | MIT | https://github.com/composer/composer |
| Microsoft Visual C++ Redistributable (x64) | latest at build time | Microsoft redistributable licence terms | https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist |

## GPL components (MariaDB, phpMyAdmin)

These programs are distributed here **unmodified**, as the vendors' own binary builds. Their complete corresponding
source code is available from the project links in the table above (for the exact version, choose the matching release
tag). If you have trouble obtaining the source, open an issue at https://github.com/arout77/Rhapsody-Server/issues and
we will help.

## Trademarks

PHP, Apache, nginx, MariaDB, phpMyAdmin, Redis and the other names above belong to their respective owners. Their
use here only identifies the software that is bundled.
