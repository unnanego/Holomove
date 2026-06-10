# Target server recovery — disk full → "Error establishing a database connection"

The target (new.holographica.space, 100 GB disk) filled up; MySQL cannot write its temp
files/binlogs and refuses connections. The fix order matters: **free a few GB first, then
restart MySQL, then reclaim the big space, then verify.** Do NOT start by deleting anything
inside `wp-content/uploads` — most of it is legitimate.

## Why the disk filled (root cause)

1. **Duplicate uploads.** The migrator retried media POSTs on timeout/500. WP writes the
   file *before* the slow thumbnail phase where this server times out, so every retry
   stored the same bytes again (`file-1.jpg`, `file-2.jpg`, …). Fixed in code now
   (uploads/creates are no-retry + a "did it land?" probe).
2. **Thumbnail multiplication.** Each upload — including every duplicate — makes WP
   generate the theme's full sub-size set (Newspaper registers many sizes + retina), so
   one duplicated original costs 10–20 extra files.
3. **Orphan files.** Uploads where PHP died mid-request left files on disk with **no
   attachment record** — invisible to the REST API and to `dedupe-media`.
4. **Logs.** `custom-fields-api.php` calls `error_log()` on every meta update; days of
   hammering a 500-ing server can grow PHP error logs / `debug.log` to tens of GB.
5. **Post revisions.** Every content re-push created a DB revision (~1.2k posts re-pushed
   per run, many runs).

## Phase 1 — get the site back (needs SSH or hosting file manager + panel)

```bash
df -h                                   # confirm 100% and which mount
# Find the hogs — run from the web root's parent:
du -xh --max-depth=2 / 2>/dev/null | sort -rh | head -30
du -sh wp-content/uploads wp-content/* | sort -rh | head -20
```

Free quick space (safe, usually several GB):

```bash
# WP + PHP logs — truncate, don't delete (handles stay open)
: > wp-content/debug.log 2>/dev/null
find . -name 'error_log' -size +50M -exec truncate -s 0 {} \;
# System logs (VPS):
journalctl --vacuum-size=100M
find /var/log -name '*.gz' -delete; truncate -s 0 /var/log/*.log
```

Restart MySQL and verify:

```bash
systemctl restart mysql      # or via hosting panel
wp db check                  # repair any crashed tables: wp db repair
```

If MySQL still won't start, check `journalctl -u mysql` — with InnoDB, disk-full almost
never corrupts data; it just needs free space to complete crash recovery.

If MySQL has binary logs (`/var/lib/mysql/binlog.*` large):

```sql
PURGE BINARY LOGS BEFORE NOW() - INTERVAL 1 DAY;
SET GLOBAL binlog_expire_logs_seconds = 259200;  -- and persist in my.cnf
```

## Phase 2 — reclaim the big space (order: cheapest/safest first)

1. **Leftover backups/installers** — check for `wp-content/updraft`, `ai1wm-backups`,
   `*.wpress`, `*.zip`, `*.tar.gz`, old `public_html_backup` style dirs. Delete.

2. **Post revisions** (DB):
   ```bash
   wp post list --post_type=revision --format=count
   wp db query "DELETE p, pm FROM wp_posts p LEFT JOIN wp_postmeta pm ON pm.post_id = p.ID WHERE p.post_type = 'revision'"
   wp db query "OPTIMIZE TABLE wp_posts, wp_postmeta"
   ```
   Then stop new ones: in `wp-config.php` add `define('WP_POST_REVISIONS', 3);`

3. **Orphan upload files** — copy `find-orphan-media.php` to the WP root and:
   ```bash
   wp eval-file find-orphan-media.php        # dry run, shows count + GB
   wp eval-file find-orphan-media.php move   # quarantine to uploads/_orphan-quarantine
   # browse the site for a few days, then:
   rm -rf wp-content/uploads/_orphan-quarantine
   ```

4. **Duplicate attachments** (the `-1`/`-2` copies that DO have records) — once the site
   is healthy, run the migrator's `dedupe-media` from this repo. It rewrites post content
   to the canonical URL and deletes the duplicates through WP, which also removes all
   their thumbnails.

5. **Excess thumbnails** (optional, biggest structural win): the theme generates 14+
   sizes per image. Unregister sizes you don't use (child-theme `functions.php`:
   `remove_image_size()` / `add_filter('intermediate_image_sizes_advanced', …)`), then
   `wp media regenerate --yes` to rebuild only the kept sizes. Also note WP keeps both
   the original AND a `-scaled` copy for images >2560px — `add_filter('big_image_size_threshold', '__return_false');`
   stops that going forward.

## Phase 3 — prevent recurrence

- The migrator no longer retries uploads/creates blindly (this commit) — duplicates stop.
- Remove or quiet `custom-fields-api.php`'s `write_log()` on the target (every meta update
  writes to error_log), or at minimum logrotate the PHP error log.
- The known ~14s `save_post` / HTTP 500 issue on target writes is the same underlying
  server weakness — worth profiling (RankMath sitemap regen + thumbnail generation are
  the usual suspects) before the next big run.
- Keep ≥15% disk free; set a hosting-panel disk alert at 80%.
