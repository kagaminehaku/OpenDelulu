#version 450
layout(set = 0, binding = 0) uniform sampler2D uTex;

layout(push_constant) uniform Push
{
    mat4 mvp;
    vec4 diffuse;
    vec4 lightAndPass;
    vec4 ambient;
    vec4 emissive;
} pc;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUV;
layout(location = 2) in float vGloss;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 texel = texture(uTex, vUV);
    float alpha = texel.a * pc.diffuse.a;
    // Pass 0: opaque texels (alpha test). 1: translucent edges only. 2: every texel (additive/darken effects).
    if (pc.lightAndPass.w < 0.5) {
        if (alpha < 0.5) discard;
    } else if (pc.lightAndPass.w < 1.5) {
        if (alpha >= 0.5 || alpha < 0.02) discard;
    } else if (alpha < 0.004) {
        discard;
    }
    // Direct3D 9 fixed-function material lighting (two-sided); keep in sync with GlModelView's shader.
    vec3 normal = normalize(vNormal);
    vec3 light = pc.lightAndPass.xyz;
    float lambert = abs(dot(normal, light));
    vec3 lit = pc.emissive.rgb + pc.ambient.rgb * 0.35 + pc.diffuse.rgb * 0.55 * lambert;
    vec3 color = texel.rgb * min(lit, vec3(1.15));
    // Sun oil: wet, darker skin with a sharp highlight. The camera direction is recovered from the light,
    // which OrbitCamera puts at normalize(eye direction + 0.6 up).
    if (vGloss > 0.001) {
        float s = 0.6 * light.y + sqrt(0.36 * light.y * light.y + 0.64);
        vec3 view = normalize(light * s - vec3(0.0, 0.6, 0.0));
        float highlight = pow(abs(dot(normal, normalize(light + view))), 48.0);
        float sheen = pow(1.0 - abs(dot(normal, view)), 3.0);
        color = color * (1.0 - 0.12 * vGloss) + pc.diffuse.rgb * vGloss * (0.75 * highlight + 0.18 * sheen);
    }
    outColor = vec4(color, alpha);
}
