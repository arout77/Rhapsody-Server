# Rhapsody Server

<p align="center"><img src="src/RhapsodyServer.Core/Web/assets/logo.png" alt="Rhapsody Server" width="360"></p>

A fast, lightweight local web-development stack for Windows. Tray icon for quick toggles, web dashboard for everything else,
no window at startup and nothing running until you ask. Built as a quicker alternative to heavier all-in-one tools,
with a one-click **Rhapsody** project scaffold.

> **Status: second prototype.** The core library, the dashboard server and the dashboard UI are tested (see "What's verified").
> The Windows-specific parts have **not** been run end to end yet. Start with `bundle.cmd --dry-run`.

## What it manages

| Piece | Details |
|---|---|
| Web server | nginx **or** Apache (switch in the dashboard). PHP via FastCGI on both. Bound to 127.0.0.1 only |
| PHP | 7.1, 7.4, 8.0, 8.1, 8.3, 8.4, 8.5 (NTS x64). **Per-site version**, plus a default |
| Database | MariaDB (LTS series), root with no password, localhost only. Each series has its own data folder |
| Cache | Redis |
| Mail | Mailpit: SMTP catcher on :1025, inbox at https://mail.test. PHP's `mail()` is pre-pointed at it |
| UI tools | phpMyAdmin at https://phpmyadmin.test (auto-login), Composer |
| Domains | `*.test` via a managed block in the hosts file, trusted HTTPS per site via mkcert |

## Two interfaces, one engine

- **Tray icon** (always there): status dots, start/stop toggles, open a site, **Open dashboard**.
- **Dashboard** (a local web page, served whenever the tray app is running), one page per job:
  **Services** (start/stop, per-service logs), **Sites** (new Rhapsody site, add an existing folder with a native **Browse…** window,
  per-site PHP), **Versions** (one dropdown per tool: install / switch / remove; Apache can also be updated or imported from a zip),
  **Modules** (PHP extensions, Apache modules, and your `php.ini`), **Logs**, **Profiler**, **Ask** (an AI assistant that investigates
  your sites), **AI help** (set up the provider) and **Help**. Light, dark or follow-the-system theme.
  Default address `http://localhost:7766`
  (`127.0.0.1:7766` still works); bookmark it. If the app is quit the browser can't connect; `rhapsody://open` starts it again. The tray's "Open dashboard" supplies the access key the first time.

Both drive the same `Stack` and share one operation lock, so they can't trip over each other.

## Sites and the public folder

"Public folder" is blank by default, which serves the project folder itself. That is right for **Rhapsody** (its `index.php` is in
the root) and anything else built that way. Type `/` for the same thing. To serve a sub-folder (Laravel's `public`), type its name.
There is no auto-detection: what you type is what is served, and a folder that doesn't exist is refused. Because a root-served site
has `.env` and `.git` inside the web root, both web servers refuse to serve dot-files and dot-folders (`.well-known` excepted).

## Rhapsody project scaffold

**Sites > New Rhapsody site** does the whole job in one action: `composer create-project` of the skeleton package (default
`arout/rhapsody-app`, changeable in settings), creates the database, writes `.env`, adds the vhost, a trusted certificate and the
hosts entry, then reloads the web server.

- **PHP 8.4.1 or newer.** Rhapsody requires it, so the PHP picker only offers versions that qualify and says so before anything is
  installed. If the chosen branch is installed but older than 8.4.1 you're told to update it on the Versions page; nothing is
  downloaded or created first, so a failed check never leaves a half-made site.
- **Database.** Created for you (name defaults to the site name with underscores). By default it uses `root`, which has no
  password in this stack; you can name a different user and password instead and the user is created with rights to that database.
  Re-running is harmless.
- **`.env`** follows a real Rhapsody `.env`: `APP_URL`, `APP_NAME`, a fresh 128-hex-character `APP_KEY`, `DB_*`, `MAIL_*` (pointed at
  Mailpit) and `REDIS_*`. Keys that already exist keep their quote style and trailing comments; missing ones are appended.
- **Composer's install scripts** (such as the Phinx migrations) only run once `.env` holds the real database credentials, so they
  connect as the right user instead of failing with "access denied".
- The site is served from the project root (Rhapsody's `index.php` is there), see "Sites and the public folder".

## PHP settings (php.ini)

On the **Modules** page, each PHP version shows the exact `php.ini` it loads, with **Edit php.ini** (opens it in your editor),
**Show in folder**, **Copy path** and, while that PHP is running, **Restart PHP x.y**. The file is generated, but everything you write **below the
"Your settings" line at the bottom is kept** whenever Rhapsody Server rewrites it (for example when you toggle an extension).
The page only names a version and an action; the app picks the file, so it can't be pointed at an arbitrary path.

## Modules

*Modules* page: PHP extensions (per PHP version, from the DLLs actually in that build) and Apache modules (from that Apache's
`modules` folder). Changes are staged, then **Apply** rewrites `php.ini` / `httpd.conf` and restarts only what needs it.
`openssl`, `curl`, `mbstring` and `mysqli` are locked on (Composer and phpMyAdmin need them), and so are the Apache modules the
generated config depends on. **PHP 8.5:** OPcache is built into PHP, so there is no `php_opcache.dll`; `php.ini` only loads it
as a module when the DLL exists, which is what fixed the *"Failed loading Zend extension 'opcache'"* error during scaffolding.

## Shipping the latest PHP, Apache, nginx and MariaDB with the app

Components are never downloaded at install time if they're in the **bundle**: a `bundle\` folder next to `RhapsodyServer.exe`
(manifest + archives). Lookup order for any component: previous download, then the bundle, then the network.
A fresh install therefore works fully offline, and "install another version" (Dashboard > Versions) uses the same
resolvers over the network. An unpacked bundle file is checksum-verified before use.

```
bundle.cmd --dry-run      1. check every vendor page, download nothing (do this first)
bundle.cmd                2. download latest PHP, Apache, nginx (stable), MariaDB (newest LTS) + tools into .\bundle
bundle.cmd --php 8.5,8.4  (optional) bundle more than the latest PHP
bundle.cmd --apache-zip "C:\Downloads\httpd-2.4.xx-...-win64-VS17.zip"
                          (if apachelounge.com blocks the script, download Apache in your browser and pass the zip)
publish.cmd               3. build RhapsodyServer.exe (self-contained, ReadyToRun) into .\dist with the bundle beside it.
                             REFUSES to build unless the bundle is complete (PHP, Apache, nginx, MariaDB and the tools);
                             publish.cmd --no-bundle makes a dev build
installer\rhapsody-server.iss    4. (optional) open in Inno Setup to make RhapsodyServer-Setup.exe (per-user, no admin needed)
installer.cmd              all of the above in one go: bundle (if needed), publish, then compile the installer with Inno Setup 6
```

Expect roughly 250-300 MB. Self-contained means users don't need to install .NET.

**Before you publish an installer publicly:** bundling means *redistributing* these programs. MariaDB and phpMyAdmin are GPL
(ship their licence text and point to source), PHP and Apache/nginx/Redis builds have their own terms, and Apache Lounge
has conditions on redistributing its builds. Read each, and add a licenses folder. I'm not a lawyer; check.

Without a `bundle\` folder next to the exe, everything is downloaded on first use. `publish.cmd` and the Inno script both stop
rather than produce an installer like that.

## Run from source (Windows, .NET 8 SDK)

```
dotnet run --project src\RhapsodyServer.Tray                       the app (add  -- --open  to open the dashboard)
dotnet run --project tests\RhapsodyServer.SmokeTests               the smoke tests (100+ checks), any OS
dotnet run --project tests\RhapsodyServer.SmokeTests -- --demo 7799 secret    the dashboard on fake data, for UI work
```

Everything lives in `%LOCALAPPDATA%\RhapsodyServer` (override: `RHAPSODY_SERVER_HOME`). Bundle location override: `RHAPSODY_SERVER_BUNDLE`.

## Help, and finding your code

- The dashboard's **Help** page says where everything is stored, each with **Open folder** and **Copy path**: the folder new sites
  are created in (default `%USERPROFILE%\Sites`), every site's own folder, the app's data folder (settings, certificates,
  downloads, databases in `data\mariadb`) and the logs. It also has a short getting-started list and answers to the usual problems.
- The **Sites** page shows where new sites are saved, with an Open folder button, plus one per site. The New Rhapsody site window
  shows the exact folder the code will go in as you type the name, and after creating a site a notice with the path and an
  **Open folder** button stays until dismissed.
- **Tray menu:** *Open sites folder* (creates it first if this is before your first site) and *Help* (opens the dashboard on the
  Help page, via `#t=TOKEN&go=help`).
- The page never sends a path. `POST /api/open` takes only a place (`sites`, `site` + name, `data`, `logs`) and the app picks the
  folder, so a request can't open or launch anything else. Folders open in Windows Explorer.

## AI help (optional, off by default)

Three features share one setup, done on the dashboard's **AI help** page:

- **Explain this**: a button on failed operations and in the log viewer. It sends the error (or the tail of a log) and shows a
  plain-language diagnosis with fix steps.
- **Inspect this request**: a button on a request in the **Profiler**. It explains where that request's time went (web server,
  PHP or SQL), flags repeated statements (N+1 queries), slow statements, large responses and SQL errors, and compares with other
  loads of the same URL to spot a cold-cache first hit. SQL is sent as its *shape* (values replaced by `?`), so row data never leaves
  your computer.
- **Ask** (its own page): type a question such as *"why is /products/123 slow?"* or *"why am I getting a 500?"*, optionally pick
  the site, and the assistant investigates instead of guessing. It works in rounds (at most 9 model calls, 4 lookups per round) and
  can use these **read-only** tools: a site health check, recent requests and slowest requests from the profiler, one request in
  full, slow SQL grouped by shape, service logs (with a filter), listing a site's folders, reading a file with line numbers, and
  searching a site's code. Optionally (a checkbox on the page, unticked until you tick it; your choice is remembered in that browser) it may also load a page from your own
  site, GET only, through your local web server. It cannot change files, restart anything or run SQL, and cannot log in or submit
  forms. Every answer comes with the list of lookups it made and what each returned, and ends with the cause, the evidence it saw
  (request numbers, timings, `file:line`) and concrete fix steps. It is not specific to Rhapsody: it works for any PHP project.
  The profiler must be on for timing and SQL questions; the assistant tells you when it isn't.
  File access is limited to the site's own folder: no `.git`, no key files (`.pem`, `.key`, `id_rsa`...), no links or junctions
  that could lead elsewhere, and secrets inside files are hidden before anything is sent.

- **Bring your own key.** Pick Groq, Google Gemini, OpenRouter, Mistral, Ollama (runs on this computer, no key, nothing leaves it)
  or any OpenAI-compatible server. Most have a free plan. The app calls the provider itself; the key never reaches the web page.
- **Backup provider.** Used only when the main one is rate-limited, down or unreachable (free plans run out). A wrong key is
  reported, not hidden by the backup.
- **Keys** are stored in `ai-keys.json`, encrypted with Windows DPAPI for your account, apart from `settings.json`. Each key is
  tied to the endpoint it was saved for and is never returned for any other, so editing a URL can't leak it to another company.
  Plain `http://` is refused except for this computer.
- **Redaction.** Before anything leaves the machine, `AiRedactor` removes passwords, tokens, API keys, `APP_KEY`, `Authorization`
  and cookie headers, credentials inside URLs, `-p`/`--password` arguments, e-mail addresses and your Windows user name. Every
  answer has a **What was sent** section showing the exact text. Pattern-based, so a secret with no recognisable label can slip
  through: that is why you can see what was sent, and why Ollama exists.
- **Free-tier friendly.** The question is capped at roughly 2,500 tokens and repeated log lines collapse into one (`x400`),
  because some free plans allow only a few thousand tokens a minute. One question runs at a time and a newer one replaces an old one.
  For **Ask**, each lookup result is capped (about 1,500 tokens), older results shrink as the conversation grows, an identical
  lookup is refused the second time, and if the run hits its limit the assistant is forced to answer with what it found.
- **Provider presets** live in `src\RhapsodyServer.Core\Ai\providers.json`. Model names and free-plan terms change often: drop a
  file named `ai-providers.json` in the data folder (same shape) to change them without rebuilding. The model field is also
  editable on the page.
- Log text, source files and page text are treated as untrusted (they can contain attacker-controlled data): the prompts say so
  and tell the model never to follow instructions found in them, and answers are rendered from DOM nodes, never as HTML.

## Dashboard security

A local web server that can start processes is a target for any web page you visit, so:
every API call needs a per-install key (the page keeps it in localStorage; it arrives in the URL *fragment*, which browsers
never send); the Host header must be 127.0.0.1 or localhost (blocks DNS rebinding); a browser-supplied Origin must be
the dashboard itself (blocks cross-site requests); the page has a strict nonce-based CSP with no external resources.
All covered by tests. Services bind to 127.0.0.1 only.

## Continuous integration

`.github/workflows/build.yml` runs on **Windows** for every push to `main` and every pull request: it builds the whole solution
(tray app included) and runs the smoke tests. Pushing a tag like `v0.3`, or running the workflow by hand, also builds the bundle
and `RhapsodyServer-Setup.exe` (the tag publishes it as a GitHub release).

## What's verified

- 116 automated tests, run on Windows in CI and on Linux: the AI helper (redaction, key store, provider client and fallback, prompt budget, API), resolvers, bundle build and offline install, version install/switch/remove guards, config generation,
  hosts-file merging, `.env` handling against a real Rhapsody `.env`, real child-process supervision, the HTTP API
  (auth, DNS-rebinding, cross-origin, 409 busy, error mapping) and the live event stream, plus the profiler: MySQL packet
  parsing, the proxy against a fake MariaDB, access-log tailing, config generation and the profile API.
- The dashboard driven in real Chromium: start/stop, logs, sites, PHP switching, validation, error banner, confirm dialog,
  Versions install/make-default, the AI page, Explain this on an error and on a log, light and dark, narrow window, locked state. No JS errors, no CSP violations.
- The Redis, Mailpit and mkcert download URLs resolve (HTTP 200 after redirects).

## What is NOT verified, and the first things to check

0. **The tray's new menu items and Explorer.** `TrayContext` (Open sites folder, Help) and `ShellFolderOpener` use Windows-only APIs
   (CI compiles them, but nobody has run them yet); the dashboard side is tested against a recording stand-in.
0. **AI help against a real provider, and DPAPI.** The provider calls are tested against a stub and the key store with a stand-in
   protector; `DpapiProtector` (a direct `crypt32` call) and real Groq/Gemini/OpenRouter/Mistral/Ollama responses have not been
   run. The preset model names are best guesses: **Save and test** tells you in one click if one is gone.

1. **Vendor page layouts.** The Apache Lounge, nginx and MariaDB-archive resolvers are written against the page shapes I
   expect; PHP's `releases.json` structure likewise. `bundle.cmd --dry-run` tells you in seconds which need a regex tweak
   (`src\RhapsodyServer.Core\Resolvers.cs`). phpMyAdmin, Composer and VC++ URLs in `Catalog.cs` are also unchecked.
2. **The tray app compiles** (CI builds it on Windows) but has not been exercised by a person yet: the menu, splash screen,
   folder picker and `rhapsody://` handler are untested in practice.
3. **Everything Windows-side:** hosts-file grant, mkcert CA install, VC++ runtime install, real nginx/Apache/MariaDB starts.
4. **Apache with several PHP workers:** the balancer route returned HTTP 500 on Apache Lounge 2.4.69, so Apache always talks to the
   first PHP worker directly (plain `proxy:fcgi://`). nginx still balances across all workers.
5. **Rhapsody scaffold:** `RhapsodyPackage` defaults to `arout/rhapsody-app`. `.env` keys follow your sample (`DB_NAME/DB_USER/DB_PASS/DB_DRIVER`, `MAIL_*`, `REDIS_*`, `APP_KEY` as 128 hex chars).
6. **Profiler on real Windows:** the TCP-table PID lookup (`WindowsConnectionOwners`), nginx/Apache actually writing `profile.tsv`
   (Apache needs 2.4.13+ for `%{end:msec}t`), and a real MariaDB client through the proxy have only been exercised against fakes.
7. The MariaDB LTS allow-list (`MariaDbResolver.LtsSeries`) needs a bump when MariaDB announces a new LTS.

## Profiler (phase 1)

Off by default; switch it on in the dashboard's **Profiler** tab. It records every request your sites serve and every SQL
statement they run, with timings, and needs nothing added to your code.

- **Requests:** nginx/Apache write a tab-separated `profile.tsv` (method, host, URI, status, bytes, total and upstream time);
  `AccessLogTailer` follows it into a bounded in-memory history.
- **SQL:** MariaDB moves to `MariaDbInternalPort` (3316) and `MySqlProxy` listens on the usual `MariaDbPort`. It is a passive
  tee: bytes are forwarded untouched in both directions and a copy is parsed (`MySqlTap`) for statement text, time to first
  byte and total time, and errors. Works for any client, not just PHP. phpMyAdmin connects straight to MariaDB so its own queries
  don't bury your app's. The server greeting has the TLS and compression bits cleared so traffic stays readable.
- **Attribution:** each connection's owning process is looked up once at connect (Windows TCP table). Queries are matched to
  requests by time window, so overlapping requests can share queries. Exact matching is phase 2.
- **Inspect this request:** select a request and press the button for an AI explanation of where its time went (see "AI help").
- Turning it on or off restarts MariaDB and the web server if they are running. Recorded SQL contains literal values, so it
  stays in memory only and behind the dashboard token.

## Renamed from Overture

Environment variables are now `RHAPSODY_SERVER_HOME` / `RHAPSODY_SERVER_BUNDLE`, the data folder is
`%LOCALAPPDATA%\RhapsodyServer`, and the hosts-file block is labelled "Rhapsody Server". A hosts block written by an older
Overture build is not recognised; delete it by hand once.

## Why it should feel fast

Native tray (no web shell), nothing starts until asked, PHP workers only for versions in use, event-driven status (no polling),
parallel installs and starts, dashboard server only while you use it, config generated and never parsed back.
Dev-tuned defaults: nginx `access_log off`, PHP `realpath_cache`, MariaDB `skip-name-resolve`, `127.0.0.1` instead of `localhost`.

## Layout

```
src/RhapsodyServer.Core      all logic + dashboard server + embedded UI (Web\index.html); runs on any OS
src/RhapsodyServer.Tray      WinForms tray app
tools/RhapsodyServer.Bundler builds the bundle
tests/                 smoke tests, fake backend, --demo mode
installer/             Inno Setup script
```

## Roadmap ideas

Profiler phase 2 (PHP prepend collector for exact request/query matching and `Server-Timing`, browser Web Vitals via a same-origin
probe, on-demand Xdebug/Lighthouse), Xdebug toggle per PHP version, update checks for bundled versions, per-site `.env` editor, auto-start with Windows (installer task exists),
optional local DNS resolver (removes the hosts file), Node/Vite process per site, Xdebug as a toggle (it is a separate download).
