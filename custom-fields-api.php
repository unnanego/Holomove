<?php
/**
 * Plugin Name: Custom Fields API
 * Plugin URI: https://holographica.space
 * Description: Meta with custom fields for the api
 * Version: 1.0
 * Author: Pavel Abdurakhimov
 */

function add_custom_fields_to_api() {
    register_rest_field('post',
        'meta',
        array(
            'get_callback' => 'get_custom_meta',
            'update_callback' => 'update_custom_meta',
            'schema' => null
        )
    );
}

add_action('rest_api_init', 'add_custom_fields_to_api');

function get_custom_meta( $post ) {
    //get the id of the post object array
    $post_id = $post['id'];
 
    //return the post meta
    return get_post_meta( $post_id );
}

function update_custom_meta($data, $post, $field_name) {
    write_log("\n______________________________________________________________________________\n");
    foreach ($data as $key => $value) {
        $val = $value[0];
        if ($key === "_thumbnail_id" && !is_array($value)) {
            write_log("Updating _thumbnail_id of post with id " . $post->ID . " with " . $value);
            update_post_meta($post->ID, $key, $value);
            continue;
        }
        
        if (unserialize($val) != false) {
            $val = unserialize($val);
        }

        update_post_meta($post->ID, $key, $val);
    }

    return;
}

function write_log ( $log )  {
    if ( is_array( $log ) || is_object( $log ) ) {
        error_log( print_r( $log, true ) );
    } else {
        error_log( $log );
    }
}