#define G 0.1

mat2 R(float a)
{
    float s = sin(a), c = cos(a);
    return mat2(c, -s, s, c);
}
vec2 H(vec2 p)
{
    p = vec2(dot(p, vec2(2127.1, 81.17)), dot(p, vec2(1269.5, 283.37)));
    return fract(sin(p) * 43758.5453);
}
float N(vec2 p)
{
    vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    return 0.5 + 0.5 * mix(mix(dot(-1.0 + 2.0 * H(i), f), dot(-1.0 + 2.0 * H(i + vec2(1, 0)), f - vec2(1, 0)), u.x), mix(dot(-1.0 + 2.0 * H(i + vec2(0, 1)), f - vec2(0, 1)), dot(-1.0 + 2.0 * H(i + vec2(1, 1)), f - vec2(1, 1)), u.x), u.y);
}
float F(vec2 uv) { return length(H(uv)); }

// Signed distance to the rounded window rect. Negative inside, positive outside.
float sdBox(vec2 p, vec2 b, float r)
{
    vec2 q = abs(p) - b + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// The original field, evaluated at a point in normalised screen space.
vec3 field(vec2 u)
{
    float a = iResolution.x / iResolution.y;

    vec2 t = u - 0.5;
    float d = N(vec2(iTime * 0.05, t.x * t.y));
    t.y /= a;
    t *= R(radians((d - 0.5) * 720.0 + 180.0));
    t.y *= a;

    float freq = 5.0, amp = 30.0, sp = iTime * 1.0;
    t.x += sin(t.y * freq + sp) / amp;
    t.y += sin(t.x * freq * 1.5 + sp) / (amp * 0.5);

    vec3 C1 = vec3(.796, .651, .969), C2 = vec3(.706, .745, .996), C3 = vec3(.961, .761, .906),
    C4 = vec3(.537, .706, .980), c5 = vec3(.192, .196, .267), c6 = vec3(.067, .067, .106);
    float cyc = sin(iTime * 0.5), tt = (sign(cyc) * pow(abs(cyc), 0.6) + 1.0) / 2.0;
    vec3 c1 = mix(C1, C3, tt), c2 = mix(C2, C4, tt), c3 = mix(C3, c5, tt), c4 = mix(C4, c6, tt);

    vec3 l1 = mix(c3, c2, smoothstep(-0.03, 0.2, (t * R(radians(-5.0))).x));
    vec3 l2 = mix(c4, c1, smoothstep(-0.02, 0.2, (t * R(radians(-5.0))).x));
    vec3 col = mix(l1, l2, smoothstep(0.05, -0.3, t.y));
    col *= 1.1;
    col = pow(col, vec3(1.1));
    return col;
}

void main()
{
    vec2 u = gl_FragCoord.xy / iResolution.xy;

    vec2 half_ = uWin.zw * 0.5;
    vec2 centre = uWin.xy + half_;
    vec2 p = gl_FragCoord.xy - centre;
    // Must match the window's border-radius (--radius), otherwise the rim
    // detaches from the corners and blobs over them.
    float radius = 4.0;

    // Distance to the window edge in pixels, plus its outward normal. Bending
    // along the normal is what a thick glass lip does to the light crossing it.
    float dist = sdBox(p, half_, radius);
    float e = 1.5;
    vec2 n = vec2(
        sdBox(p + vec2(e, 0.0), half_, radius) - sdBox(p - vec2(e, 0.0), half_, radius),
        sdBox(p + vec2(0.0, e), half_, radius) - sdBox(p - vec2(0.0, e), half_, radius)
    );
    n = normalize(n + vec2(1e-6));

    // Lens profile: nothing in the middle, a tight bend right on the rim.
    float band = 68.0;
    float near = 1.0 - smoothstep(0.0, band, abs(dist));
    near *= near;

    // Displace along the normal, in pixels, then convert to screen units.
    float push = near * 17.0;
    vec2 off = n * push / iResolution.xy;

    // Anchor the hue to one refracted sample. Sampling each channel from far
    // apart fabricates colours that are not in the field (this palette has a
    // near-constant green, so wide R/B separation invents yellow), so the
    // dispersion stays small and is blended back over the base.
    vec3 base = field(u + off);
    float disp = near * 2.5 / iResolution.x;
    vec3 col;
    col.r = field(u + off * 0.95 + vec2(disp, 0.0)).r;
    col.g = base.g;
    col.b = field(u + off * 1.05 - vec2(disp, 0.0)).b;
    col = mix(base, col, 0.4);

    // Specular lip: a thin line hugging the border. The highlight brightens the
    // shader's own colour by scaling it and capping so no channel clips.
    // Adding white instead is what turned blue areas into a yellow-green rim.
    float rim = pow(1.0 - smoothstep(0.0, 7.0, abs(dist)), 1.5);

    // The highlight tracks the pointer so it slides around the rim.
    vec2 toMouse = (iMouse * iResolution.xy) - centre;
    float lobe = clamp(dot(normalize(toMouse + vec2(1e-5)), n) * 0.5 + 0.5, 0.0, 1.0);

    float peak = max(col.r, max(col.g, col.b));
    vec3 lifted = col * min(2.1, 1.0 / max(peak, 1e-3));
    col = mix(col, lifted, rim * (0.55 + 0.45 * lobe));

    // Soft bloom: a wider, gentler falloff around the lip so it reads as light
    // rather than a drawn line. Multiplied, so the halo keeps the shader's hue.
    float bloom = pow(1.0 - smoothstep(2.0, 18.0, abs(dist)), 2.6);
    col += col * bloom * (0.16 + 0.22 * lobe);

    // Hairline contact shadow at the very edge reads as glass thickness.
    float edge = 1.0 - smoothstep(0.0, 2.5, abs(dist + 1.0));
    col *= 1.0 - edge * 0.45;

    col -= F(u) * G;
    gl_FragColor = vec4(col, 1.0);
}