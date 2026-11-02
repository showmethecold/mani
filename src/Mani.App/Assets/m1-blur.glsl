//!PARAM rect_x
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM rect_y
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.70

//!PARAM rect_w
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
1.0

//!PARAM rect_h
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.30

//!PARAM rect_corner
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 200.0
0.0

//!PARAM rect_amount
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 1.0
1.0

//!PARAM side_x
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM side_y
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM side_w
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM side_h
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM side_corner
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 200.0
0.0

//!PARAM side_amount
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.0

//!PARAM osd_x
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM osd_y
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM osd_w
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM osd_h
//!TYPE float
//!MINIMUM -10.0
//!MAXIMUM 10.0
0.0

//!PARAM osd_corner
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 200.0
0.0

//!PARAM osd_amount
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.0

//!HOOK SCALED
//!BIND HOOKED
//!SAVE MANI_BLUR_X
//!DESC Mani M1 horizontal GPU blur
vec4 hook() {
    vec4 color = vec4(0.0);
    float weights[5] = float[5](0.227027, 0.194595, 0.121622, 0.054054, 0.016216);
    color = HOOKED_tex(HOOKED_pos) * weights[0];
    for (int i = 1; i < 5; ++i) {
        vec2 offset = vec2(float(i) * 3.0, 0.0) * HOOKED_pt;
        color += (HOOKED_tex(HOOKED_pos + offset) + HOOKED_tex(HOOKED_pos - offset)) * weights[i];
    }
    return color;
}

//!HOOK SCALED
//!BIND HOOKED
//!BIND MANI_BLUR_X
//!DESC Mani rounded glass surfaces (shared GPU blur)
float glass_mask(vec4 bounds, float corner, float amount) {
    if (amount <= 0.0 || bounds.z <= 0.0 || bounds.w <= 0.0) return 0.0;
    vec2 half_size = bounds.zw * HOOKED_size * 0.5;
    vec2 center = (bounds.xy + bounds.zw * 0.5) * HOOKED_size;
    float radius = min(corner, min(half_size.x, half_size.y));
    vec2 q = abs(HOOKED_pos * HOOKED_size - center) - half_size + radius;
    float distance = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - radius;
    return (1.0 - smoothstep(-1.0, 1.0, distance)) * amount;
}
vec4 hook() {
    vec4 original = HOOKED_tex(HOOKED_pos);
    float mask = max(glass_mask(vec4(rect_x, rect_y, rect_w, rect_h), rect_corner, rect_amount),
        max(glass_mask(vec4(side_x, side_y, side_w, side_h), side_corner, side_amount),
            glass_mask(vec4(osd_x, osd_y, osd_w, osd_h), osd_corner, osd_amount)));
    if (mask <= 0.0) return original;
    float weights[5] = float[5](0.227027, 0.194595, 0.121622, 0.054054, 0.016216);
    vec4 color = MANI_BLUR_X_tex(MANI_BLUR_X_pos) * weights[0];
    for (int i = 1; i < 5; ++i) {
        vec2 offset = vec2(0.0, float(i) * 3.0) * MANI_BLUR_X_pt;
        color += (MANI_BLUR_X_tex(MANI_BLUR_X_pos + offset) + MANI_BLUR_X_tex(MANI_BLUR_X_pos - offset)) * weights[i];
    }
    return mix(original, color, mask);
}
