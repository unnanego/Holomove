# New server setup + migration runbook

End-to-end steps to stand up a fresh server and migrate a WordPress site onto it with
the **Holomove** migrator, written from the lessons of the holographica.space migration.
Every section marked **⚠ lesson** is something that actually bit us — do it up front.

> Target reference spec: 8 GB RAM / 4 vCPU VPS, Ubuntu 24.04, ~100 GB disk, serving a
> ~8,000-post WordPress site (Newspaper/tagDiv theme, image/video heavy).
> For *recovering* a broken/over-full server, see [`server-recovery/RUNBOOK.md`](server-recovery/RUNBOOK.md).

---

## 0. TL;DR order of operations

1. Provision server, base OS hardening.
2. **Swap** + **BBR** (two kernel-level wins — do these first, they prevent the two worst problems).
3. Install web stack — **nginx + certbot** *or* **Caddy** (recommended; see §7).
4. PHP-FPM + MariaDB tuning sized to the RAM.
5. Install WordPress + theme; trim image sizes; cap revisions; page cache.
6. **Put up the migration outbound-block** (stops save_post hooks hanging) — §9.
7. Run the migrator (`Holomove migrate`), then `finalize`.
8. Go-live: DNS + `siteurl`/`home` + `search-replace` + **remove the blocks** + TLS for the live domain.
9. Post-launch cleanup.

---

## 1. Provision & base hardening

```bash
# as root on a fresh Ubuntu box
apt update && apt -y upgrade
timedatectl set-timezone UTC
# a non-root sudo user + SSH keys are recommended; keep password auth off if possible
# fail2ban for SSH (we saw 1000+ brute-force attempts):
apt -y install fail2ban
```

Firewall: allow 22/80/443 only. Leave `INPUT` open to web (don't geo-block — it just causes
"site won't load on VPN" confusion). The one outbound block we *do* add is temporary and
migration-only (§9).

---

## 2. Swap  ⚠ lesson (prevents OOM crashes)

Without swap, a PHP-FPM memory spike during migration triggers the OOM killer and the site
"hangs"/drops. 4 GB swapfile as a safety net:

```bash
fallocate -l 4G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=4096
chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf && sysctl -p /etc/sysctl.d/99-swappiness.conf
```

`swappiness=10` = use swap only under real pressure, not for routine paging.

---

## 3. BBR  ⚠ lesson (fixes "downloads are slow despite fast server")

Default `cubic` congestion control collapses throughput on any path with latency/loss — we
measured 2.4 MB/s on large files; BBR took it to 10+ MB/s and sped up the whole site.

```bash
modprobe tcp_bbr
echo tcp_bbr > /etc/modules-load.d/bbr.conf
printf 'net.core.default_qdisc=fq\nnet.ipv4.tcp_congestion_control=bbr\n' > /etc/sysctl.d/99-bbr.conf
sysctl -p /etc/sysctl.d/99-bbr.conf
sysctl -n net.ipv4.tcp_congestion_control   # -> bbr
```

---

## 4. PHP 8.3 + extensions

```bash
apt -y install php8.3-fpm php8.3-mysql php8.3-curl php8.3-gd php8.3-imagick \
  php8.3-xml php8.3-mbstring php8.3-zip php8.3-intl php8.3-bcmath php8.3-redis
# imagick needs ghostscript for PDF thumbnails:
apt -y install ghostscript
```

### PHP-FPM pool — `/etc/php/8.3/fpm/pool.d/www.conf`  ⚠ lesson (the OOM root cause)

`pm.max_children × memory_limit` must stay **under** RAM. We had `25 × 512M = 12.5 GB` on an
8 GB box — that's what OOM-killed PHP. Size it:

```ini
pm = dynamic
pm.max_children = 12          ; with memory_limit=256M -> ~3 GB worst case (safe on 8 GB)
pm.start_servers = 4
pm.min_spare_servers = 3
pm.max_spare_servers = 8
pm.max_requests = 500         ; recycle workers, guards leaks
request_terminate_timeout = 150
slowlog = /var/log/php8.3-fpm.slow.log
request_slowlog_timeout = 5s  ; backtraces of slow requests — invaluable for diagnosing hangs
```

### `php.ini` (fpm)

```ini
memory_limit = 256M
max_execution_time = 120
upload_max_filesize = 128M
post_max_size = 128M
; OPcache (big WP win):
opcache.enable = 1
opcache.memory_consumption = 192
opcache.interned_strings_buffer = 32
opcache.max_accelerated_files = 20000
opcache.revalidate_freq = 60
; quiet + rotate the error log (days of error_log() spam helped fill the disk once):
log_errors = On
error_log = /var/log/php/error.log
```

Add a logrotate entry for `/var/log/php/error.log` (daily, `maxsize 100M`, rotate 3).

---

## 5. MariaDB

```bash
apt -y install mariadb-server
```

`/etc/mysql/mariadb.conf.d/99-tuning.cnf` (create it — see [the note below]):

```ini
[mysqld]
innodb_buffer_pool_size        = 3G
innodb_log_file_size           = 512M
innodb_flush_method            = O_DIRECT
innodb_flush_log_at_trx_commit = 2      ; ~1s durability window for much faster writes
max_connections                = 100
tmp_table_size                 = 64M
max_heap_table_size            = 64M
binlog_expire_logs_seconds     = 259200 ; 3 days (binlogs were part of a disk-full incident)
slow_query_log                 = 1
slow_query_log_file            = /var/log/mysql/slow.log
long_query_time                = 2
```

`99-tuning.cnf` doesn't ship by default — you create it. It's read because `my.cnf` has an
`!includedir`. On RHEL-family the dir is `/etc/my.cnf.d/`. Then:

```bash
mkdir -p /var/log/mysql && chown mysql:mysql /var/log/mysql
systemctl restart mariadb
mysql -e "SHOW VARIABLES LIKE 'innodb_buffer_pool_size'"   # expect 3221225472
```

Create the DB + user for WordPress as usual.

---

## 6. nginx (if not using Caddy — see §7)

`/etc/nginx/nginx.conf` http block essentials:

```nginx
sendfile on; tcp_nopush on;
client_max_body_size 128m;            # match post_max_size (raise if you host big videos)
keepalive_timeout 15;
gzip on; gzip_vary on; gzip_min_length 1024;
gzip_types text/css application/javascript application/json application/xml image/svg+xml text/plain;
```

**Serve all hostnames from one site block via a shared snippet** — this avoids the trap we hit
where certbot created a stray `server_name x; return 444;` stub that 404'd one domain.

`/etc/nginx/snippets/wp-site.conf`:

```nginx
root /var/www/html;
index index.php index.html;
location / { try_files $uri $uri/ /index.php?$args; }
# security: no PHP from uploads, kill xmlrpc, hide dotfiles (except ACME)
location ~* /wp-content/uploads/.*\.php$ { deny all; }
location = /xmlrpc.php { deny all; }
location ~ /\.(?!well-known) { deny all; }
# long static cache (big bandwidth saver on media-heavy sites)
location ~* \.(jpg|jpeg|png|gif|webp|avif|svg|ico|css|js|woff2?|mp4|webm|ogg)$ {
    try_files $uri =404; expires 30d; add_header Cache-Control "public"; access_log off;
}
location ~ \.php$ {
    include snippets/fastcgi-php.conf;
    fastcgi_pass unix:/run/php/php8.3-fpm.sock;
    fastcgi_read_timeout 300s;        # the theme's save_post/thumbnailing can be slow
    fastcgi_buffers 16 16k; fastcgi_buffer_size 32k;
}
```

`/etc/nginx/sites-enabled/default` (one 80→443 redirect block + one 443 block per cert,
each `include snippets/wp-site.conf;`). Then TLS:

```bash
apt -y install certbot python3-certbot-nginx
certbot --nginx -d example.com -d www.example.com --redirect
```

⚠ When you later add the *live* domain, get its cert too and add the names to the **same**
443 server block (don't let certbot leave an empty stub).

---

## 7. Caddy — recommended alternative to nginx + certbot

Most of the TLS/vhost pain in this migration (the `return 444` stub, cert-only-covers-one-domain,
manual renewal config) **does not exist with Caddy** — it gets and renews certs automatically
for every hostname in the config, redirects HTTP→HTTPS by default, and speaks HTTP/3.

```bash
apt -y install debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list
apt update && apt -y install caddy
```

`/etc/caddy/Caddyfile` for WordPress + PHP-FPM:

```caddyfile
example.com, www.example.com {
    root * /var/www/html
    encode zstd gzip
    php_fastcgi unix//run/php/php8.3-fpm.sock
    file_server

    @uploadsphp path_regexp \.php$
    @inuploads path /wp-content/uploads/*
    handle @inuploads { @php path *.php; respond @php 403 }
    @dotfiles { path /.* ; not path /.well-known/* }
    respond @dotfiles 403

    @static path *.jpg *.jpeg *.png *.gif *.webp *.svg *.css *.js *.woff2 *.mp4 *.webm
    header @static Cache-Control "public, max-age=2592000"
    request_body { max_size 128MB }
}
```

`systemctl reload caddy` — that's it; certs for both names are issued/renewed automatically.

**Tradeoffs:** Caddy is simpler and far less footgun-prone for HTTPS, but nginx has more
copy-paste WP tutorials and is what most hosts ship. For a *new* build, Caddy is the better
default. (Note: BBR/swap/PHP-FPM/MariaDB tuning above are identical either way.)

---

## 8. WordPress + theme config  ⚠ several lessons

After installing WordPress + the theme (Newspaper/tagDiv) and `wp-config.php`:

```bash
# cap post revisions (we deleted 43,917 of them post-migration):
wp config set WP_POST_REVISIONS 5 --raw --type=constant
```

In the child theme `functions.php` (or an mu-plugin):

```php
// Newspaper registers 14+ image sizes + retina → every upload multiplies into many files
// (a disk-fill driver, and slow thumbnail generation that stalls save_post). Drop unused sizes:
add_filter('intermediate_image_sizes_advanced', function ($sizes) {
    foreach (['unused-size-a','unused-size-b'] as $k) unset($sizes[$k]);  // keep only what the theme shows
    return $sizes;
});
// Stop WP keeping BOTH the original and a -scaled copy of >2560px images:
add_filter('big_image_size_threshold', '__return_false');
```

**Caching:** WP Super Cache (Simple mode) + an nginx/Caddy rule to serve the cached HTML
without PHP defeats bot storms on uncached URLs. Optional: Redis object cache
(`apt install redis-server`, the *Redis Object Cache* plugin, click Enable — needs the
`php8.3-redis` ext installed in §4). Bot rate-limiting (`limit_req` / Caddy `rate_limit`) is
worth adding before a big crawl.

---

## 9. DURING migration — the outbound block  ⚠ the #1 hang cause

The theme's `save_post` hooks (tagDiv cloud, oEmbed of Telegram/VK/YouTube, remote thumbnail
fetches of not-yet-rewritten source URLs) make **blocking outbound HTTP on every post save**.
When the source is slow/unreachable, each save hangs a PHP worker up to the client timeout and
the whole migration grinds. Block PHP's outbound HTTP **for the duration of the migration only**:

```bash
# network-level (catches even the theme's direct cURL that bypasses WP's HTTP API):
iptables -I OUTPUT -m owner --uid-owner www-data -p tcp --dport 443 -j REJECT
iptables -I OUTPUT -m owner --uid-owner www-data -p tcp --dport 80  -j REJECT
```

and/or an mu-plugin `wp-content/mu-plugins/zzz-block-outbound.php`:

```php
<?php // MIGRATION ONLY — delete at go-live
add_filter('pre_http_request', function ($pre, $args, $url) {
    $h = strtolower(parse_url($url, PHP_URL_HOST) ?: '');
    if (in_array($h, ['', 'localhost', '127.0.0.1', 'YOUR-LIVE-DOMAIN'], true)) return $pre;
    return new WP_Error('blocked', 'outbound blocked during migration');
}, 0, 3);
add_filter('http_request_timeout', fn() => 10, 100);  // hard cap so nothing hangs
```

**Remove both at go-live** (step §11) or the live site can't do oEmbed/plugin/API calls.

Also keep the migrator's upload path **no-retry** (it is, as of commit 37501a2): retried
media POSTs duplicate the file + its whole thumbnail set after the slow thumbnail phase and
filled the disk once.

---

## 10. Run the migrator + known data gotchas  ⚠ lessons

```bash
Holomove setup       # configure source/target/canonical domains + creds (settings.json, gitignored)
Holomove migrate     # source -> backup -> target (idempotent; safe to re-run)
Holomove finalize    # rewrite media URLs target-domain -> canonical (run before DNS swap)
```

Things that went wrong and what to watch / pre-empt:

- **Non-ASCII (Cyrillic) media filenames** get sanitized on upload (`Снимок….jpg` →
  `2026-05-21-….jpg`; all-Cyrillic names collapse to `unnamed-file-N.*`), but content URLs keep
  the original name → 404. Recovery used **content-hash matching against the still-live old
  source** (download original, MD5-match to the target file in the same dir). *Keep the old
  source online until everything's verified* — this and the recoveries below all depend on it.
- **Author attribution**: the migrator fell back to the admin account when it couldn't resolve
  the source author at *create* time → ~3,500 posts mislabeled. Map authors by **slug/nicename**
  (source/target user IDs don't align), and never silently fall back to id 1. Source is authority.
- **Featured images**: ~500 posts came over with no `_thumbnail_id`. Re-derive from the source
  post's `featured_media` (match by filename → content-hash → re-fetch). Some are genuine
  link-rot (source attachment deleted years ago) and unrecoverable.
- **Tag/category matching**: key target lookup dicts by a **normalized** slug
  (`Uri.UnescapeDataString` + lowercase) — raw percent-encoded Cyrillic slugs otherwise never
  match and posts re-push their taxonomy forever (fixed: `SlugComparer`).
- **Paged REST reads**: retry transient 5xx/timeout instead of silently dropping a whole page,
  or the in-memory media index gets holes and content URLs can't be rewritten (fixed in
  `FetchPageAsync`).
- **`finalize`** only rewrites `https://`-prefixed `/wp-content/` URLs; finish the tail with a
  DB `REPLACE`/`search-replace` (see §11) to catch protocol-relative and any it didn't queue.
- **Videos**: shortcodes arrive as pre-expanded `<div class="wp-video">…<video width=696>` HTML,
  so WP never enqueues `wp-mediaelement.css` → videos overflow on mobile. Add responsive CSS
  (Customizer → Additional CSS): `.wp-video,.wp-video-shortcode{max-width:100%!important;height:auto!important;margin:auto}`.

---

## 11. Go-live / domain swap

```bash
# 1. point DNS (apex + www) at the new server; confirm propagation:
dig +short A example.com @8.8.8.8        # == new IP, and ONLY the new IP (remove the old A record!)

# 2. WordPress canonical URLs:
wp option update siteurl https://example.com
wp option update home    https://example.com

# 3. fix the metadata/serialized tail (post content done by finalize; this gets options,
#    postmeta, SEO canonical/OG, rank_math tables, etc.). --skip-columns=guid is important:
wp db export /root/pre-swap-$(date +%F).sql.gz   # backup first
wp search-replace 'staging.example.com' 'example.com' --all-tables-with-prefix --skip-columns=guid

# 4. TLS for the live domain (nginx: certbot; Caddy: automatic, nothing to do)
certbot --nginx -d example.com -d www.example.com --redirect

# 5. REMOVE the migration blocks (critical):
iptables -D OUTPUT -m owner --uid-owner www-data -p tcp --dport 443 -j REJECT
iptables -D OUTPUT -m owner --uid-owner www-data -p tcp --dport 80  -j REJECT
rm -f /var/www/html/wp-content/mu-plugins/zzz-block-outbound.php

# 6. verify
curl -sI https://example.com/ | head -1            # 200
```

⚠ Don't change `siteurl`/`home` before DNS points at the new box, or the staging site
redirects to a domain still serving the old site.

---

## 12. Post-launch

```bash
wp db query "DELETE a,b,c FROM wp_posts a
  LEFT JOIN wp_term_relationships b ON a.ID=b.object_id
  LEFT JOIN wp_postmeta c ON a.ID=c.post_id WHERE a.post_type='revision'"   # prune revision bloat
```

- Keep the **old source server online** for a while — every content/media/author recovery above
  relies on it as the source of truth.
- Watch `df -h` (keep ≥15% free), `free -h` (swap should sit near-idle), and the PHP-FPM slow log.
- Consider converting large animated GIFs to MP4 (`ffmpeg`) — they're 20–40× smaller and a big
  bandwidth/disk win on media-heavy sites.

---

*Recovery (disk full, DB won't start, duplicate uploads, orphan media):* see
[`server-recovery/RUNBOOK.md`](server-recovery/RUNBOOK.md).
