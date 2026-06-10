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
    foreach ($data as $key => $value) {
        if ($key === "_thumbnail_id" && !is_array($value)) {
            update_post_meta($post->ID, $key, $value);
            continue;
        }

        $val = is_array($value) ? $value[0] : $value;
        update_post_meta($post->ID, $key, maybe_unserialize($val));
    }

    return;
}