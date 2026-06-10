<?php
/**
 * Finds (and optionally quarantines) files in wp-content/uploads that no attachment
 * record references — the leftovers of uploads where PHP died after writing the file
 * but before/while creating the attachment row or its thumbnails. These are invisible
 * to the REST API and to dedupe-media, and are the bulk of unexplained disk usage.
 *
 * Usage (on the server, in the WP root):
 *   wp eval-file find-orphan-media.php            # dry run: list + total size
 *   wp eval-file find-orphan-media.php move       # move orphans to uploads/_orphan-quarantine/
 *
 * After verifying the site still looks right for a few days:
 *   rm -rf wp-content/uploads/_orphan-quarantine
 */

global $wpdb;

$mode   = isset( $args[0] ) ? $args[0] : 'list';
$upload = wp_get_upload_dir();
$base   = trailingslashit( $upload['basedir'] );

// Build the set of every file path WordPress knows about, relative to uploads/.
$known = array();

foreach ( $wpdb->get_col( "SELECT meta_value FROM {$wpdb->postmeta} WHERE meta_key = '_wp_attached_file'" ) as $rel ) {
	$known[ strtolower( $rel ) ] = true;
}

foreach ( $wpdb->get_col( "SELECT meta_value FROM {$wpdb->postmeta} WHERE meta_key = '_wp_attachment_metadata'" ) as $raw ) {
	$meta = @unserialize( $raw );
	if ( ! is_array( $meta ) ) {
		continue;
	}
	$file = isset( $meta['file'] ) ? $meta['file'] : '';
	$dir  = $file ? trailingslashit( dirname( $file ) ) : '';
	if ( $file ) {
		$known[ strtolower( $file ) ] = true;
	}
	if ( ! empty( $meta['original_image'] ) ) {
		$known[ strtolower( $dir . $meta['original_image'] ) ] = true;
	}
	if ( ! empty( $meta['sizes'] ) && is_array( $meta['sizes'] ) ) {
		foreach ( $meta['sizes'] as $size ) {
			if ( ! empty( $size['file'] ) ) {
				$known[ strtolower( $dir . $size['file'] ) ] = true;
			}
		}
	}
}

echo 'Known attachment files (incl. registered sizes): ' . count( $known ) . "\n";

$orphans = array();
$bytes   = 0;

$it = new RecursiveIteratorIterator(
	new RecursiveDirectoryIterator( $base, FilesystemIterator::SKIP_DOTS )
);
foreach ( $it as $f ) {
	if ( ! $f->isFile() ) {
		continue;
	}
	$rel = str_replace( '\\', '/', substr( $f->getPathname(), strlen( $base ) ) );

	// Only touch the standard year/month upload tree — never plugin/theme dirs in uploads.
	if ( ! preg_match( '#^\d{4}/\d{2}/#', $rel ) ) {
		continue;
	}
	$low = strtolower( $rel );
	if ( isset( $known[ $low ] ) ) {
		continue;
	}

	// Size variant (foo-300x200.jpg) of a known base file? The theme can generate sizes
	// not present in the stored metadata, so strip the suffix and check the base too.
	$stripped = preg_replace( '#-\d+x\d+(\.[a-z0-9]+)$#', '$1', $low );
	if ( $stripped !== $low && isset( $known[ $stripped ] ) ) {
		continue;
	}

	$orphans[] = $rel;
	$bytes    += $f->getSize();
}

printf( "Orphan files: %d, total %.2f GB\n", count( $orphans ), $bytes / 1024 / 1024 / 1024 );

if ( 'move' !== $mode ) {
	foreach ( array_slice( $orphans, 0, 50 ) as $rel ) {
		echo "  $rel\n";
	}
	if ( count( $orphans ) > 50 ) {
		echo '  … and ' . ( count( $orphans ) - 50 ) . " more (run with 'move' to quarantine all)\n";
	}
	exit;
}

$quarantine = $base . '_orphan-quarantine/';
$moved      = 0;
foreach ( $orphans as $rel ) {
	$dest = $quarantine . $rel;
	if ( ! is_dir( dirname( $dest ) ) ) {
		mkdir( dirname( $dest ), 0755, true );
	}
	if ( @rename( $base . $rel, $dest ) ) {
		$moved++;
	}
}
printf( "Moved %d/%d orphan files to %s\n", $moved, count( $orphans ), $quarantine );
