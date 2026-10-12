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

// ---------------------------------------------------------------------------
// Two programs are built from this file.
//
// Without RIM it is the page background: the field and nothing else. Nothing is
// drawn around the window -- no frame, no glow, no shadow.
//
// With RIM it is drawn on a canvas laid over the window itself, and paints only
// the window's own edge: the last uBand pixels before the border, transparent
// everywhere else. That edge is treated as glass. The window is a pane whose
// rim is rounded over, level a little way in and rolling off to vertical at the
// border, and light does two things there.
//
//   Refraction. A ray coming straight down meets the tilted surface, is bent on
//   entering the glass (Snell's law) and reaches the page a little way from
//   where it was heading; the steeper the surface, the further. So the rim
//   shows the background that is right behind it, pulled in and compressed,
//   clear and bright where the rest of the window is frosted and dark.
//
//   Reflection. The steep outer part of the rim throws light back, and what it
//   throws back is the light that reached it: the colour of the page at that
//   spot, at full strength. Over pink it is a pink line, over blue a blue one.
//   It is never white, because nothing here lights the glass but the page.
//
// Both run the whole way round the border and take their colour from whatever
// the background is doing underneath, so the rim shifts as the field moves. The
// pointer only leans on it: the side facing the pointer is a little brighter.
// Both fade out toward the inside of the window, so there is no line where the
// rim stops.
// ---------------------------------------------------------------------------

#ifdef RIM

const float IOR = 1.50;    // glass
const float DEPTH = 34.0;  // pixels a ray travels inside the glass

// Where a ray going straight down ends up once the surface with normal n has
// bent it, as an offset in pixels.
vec2 bend(vec3 n, float ior)
{
    vec3 r = refract(vec3(0.0, 0.0, -1.0), n, 1.0 / ior);
    return r.xy / max(-r.z, 0.25) * DEPTH;
}

void main()
{
    vec2 half_ = uSize * 0.5;
    vec2 p = gl_FragCoord.xy - half_;

    // How far into the rim this pixel is on each axis: 0 where the rim begins,
    // 1 on the border.
    vec2 q = (abs(p) - (half_ - uBand)) / uBand;
    vec2 qp = max(q, 0.0);

    // One smooth surface round the corner rather than two planes meeting in a
    // crease, with a power high enough that the outline stays square.
    float x = pow(pow(qp.x, 16.0) + pow(qp.y, 16.0), 1.0 / 16.0);
    if (x <= 0.001)
    {
        gl_FragColor = vec4(0.0);
        return;
    }
    x = min(x, 1.0);

    // Which way the surface leans, turned gently through the corner.
    vec2 n2 = normalize(qp * qp + vec2(1e-5)) * sign(p);

    // Circular cross-section: the slope runs from level to vertical.
    float slope = x / sqrt(max(1.0 - x * x, 1e-3));
    vec3 n = normalize(vec3(n2 * min(slope, 9.0), 1.0));

    // This pixel's place on the page, which is where the background was drawn.
    vec2 u = (gl_FragCoord.xy + uOrigin) / iResolution.xy;
    vec2 px = 1.0 / iResolution.xy;

    // Refraction. One sample for all three channels: splitting them by colour
    // reads red from one place and blue from another, and over this palette
    // that manufactures yellows and greens the background never contains.
    vec3 glass = field(u + bend(n, IOR) * px);

    // The same colour at full strength: scaled until its strongest channel is
    // as bright as it can go, which keeps the hue exactly and only raises the
    // light. Capped, so the darkest parts of the field lift to a deep blue
    // rather than being forced all the way up into noise.
    float peak = max(glass.r, max(glass.g, glass.b));
    vec3 lifted = glass * min(4.5, 1.0 / max(peak, 1e-3));

    // The pointer leans on the rim: the side facing it is brighter, the far
    // side keeps most of its light. Never less than three quarters.
    vec2 toPointer = (iMouse * iResolution.xy) - (gl_FragCoord.xy + uOrigin);
    float facing = dot(n2, normalize(toPointer + vec2(1e-4))) * 0.5 + 0.5;
    float lean = 0.76 + 0.24 * facing;

    // The highlight: a hair-thin line just inside the border, where the
    // surface is steep enough to throw light straight back.
    float rimPx = (1.0 - x) * uBand;
    float streak = exp(-pow((rimPx - 0.9 * uScale) / (0.8 * uScale), 2.0));

    // Fresnel: the steeper the surface, the more of the light it returns
    // instead of passing. So the refracted page gives way to its own lifted
    // colour toward the border.
    float f0 = pow((IOR - 1.0) / (IOR + 1.0), 2.0);
    float fresnel = f0 + (1.0 - f0) * pow(x, 3.0);

    vec3 col = mix(glass * 1.1, lifted, fresnel * 0.75);
    col = mix(col, lifted, streak);

    // How much of the rim shows: a lens that rises smoothly from nothing at
    // the inside to nearly solid at the border, with the line on top of it.
    // The same everywhere along the border but for the lean.
    float lens = pow(x, 2.0) * 0.85;
    float alpha = clamp(max(lens, streak) * lean, 0.0, 1.0);

    col = clamp(col - F(u) * G * 0.6, 0.0, 1.0);
    gl_FragColor = vec4(col * alpha, alpha);
}

#else

void main()
{
    vec2 u = gl_FragCoord.xy / iResolution.xy;
    vec3 col = field(u);
    col -= F(u) * G;
    gl_FragColor = vec4(col, 1.0);
}

#endif
