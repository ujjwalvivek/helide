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

void main()
{
    vec2 f = gl_FragCoord.xy, u = f / iResolution.xy;
    float a = iResolution.x / iResolution.y;
    // Transformed uv
    vec2 t = u - 0.5;
    float d = N(vec2(iTime * 0.05, t.x * t.y));
    t.y /= a;
    t *= R(radians((d - 0.5) * 720.0 + 180.0));
    t.y *= a;
    // Wave warp
    float freq = 5.0, amp = 30.0, sp = iTime * 1.0;
    t.x += sin(t.y * freq + sp) / amp;
    t.y += sin(t.x * freq * 1.5 + sp) / (amp * 0.5);
    // Gradient colors
    vec3 C1 = vec3(.796, .651, .969), C2 = vec3(.706, .745, .996), C3 = vec3(.961, .761, .906),
    C4 = vec3(.537, .706, .980), c5 = vec3(.192, .196, .267), c6 = vec3(.067, .067, .106);
    float cyc = sin(iTime * 0.5), tt = (sign(cyc) * pow(abs(cyc), 0.6) + 1.0) / 2.0;
    vec3 c1 = mix(C1, C3, tt), c2 = mix(C2, C4, tt), c3 = mix(C3, c5, tt), c4 = mix(C4, c6, tt);
    // Blend layers
    vec3 l1 = mix(c3, c2, smoothstep(-0.03, 0.2, (t * R(radians(-5.0))).x));
    vec3 l2 = mix(c4, c1, smoothstep(-0.02, 0.2, (t * R(radians(-5.0))).x));
    vec3 col = mix(l1, l2, smoothstep(0.05, -0.3, t.y));
    // Brightness & contrast
    col *= 1.1;
    col = pow(col, vec3(1.1));
    // Film grain
    col -= F(u) * G;
    gl_FragColor = vec4(col, 1.0);
}
