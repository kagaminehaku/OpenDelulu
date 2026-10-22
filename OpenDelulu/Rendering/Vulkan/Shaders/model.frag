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
layout(location = 3) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 texel = texture(uTex, vUV);
    float alpha = texel.a * pc.diffuse.a;
    // Pass 0: opaque texels (alpha test). 1: translucent edges only. 2: every texel (additive/darken effects).
    // 3 + colour: the glare source (every texel whatever its alpha, see the end).
    if (pc.lightAndPass.w < 0.5) {
        if (alpha < 0.5) discard;
    } else if (pc.lightAndPass.w < 1.5) {
        if (alpha >= 0.5 || alpha < 0.02) discard;
    } else if (pc.lightAndPass.w < 2.5 && alpha < 0.004) {
        discard;
    }
    // Direct3D 9 fixed-function material lighting (two-sided); keep in sync with GlModelView's shader.
    vec3 normal = normalize(vNormal);
    vec3 light = pc.lightAndPass.xyz;
    float lambert = abs(dot(normal, light));
    vec3 lit = pc.emissive.rgb + pc.ambient.rgb * 0.35 + pc.diffuse.rgb * 0.55 * lambert;
    // Objects with .xl lights: the per-vertex Direct3D 9 colour (already clamped and tinted) modulates the texture.
    vec3 color = vColor.a > 0.5 ? texel.rgb * vColor.rgb : texel.rgb * min(lit, vec3(1.15));
    // Sun oil: wet, darker skin with a sharp highlight. The camera direction is recovered from the light,
    // which OrbitCamera puts at normalize(eye direction + 0.6 up).
    if (vGloss > 0.001) {
        float s = 0.6 * light.y + sqrt(0.36 * light.y * light.y + 0.64);
        vec3 view = normalize(light * s - vec3(0.0, 0.6, 0.0));
        float highlight = pow(abs(dot(normal, normalize(light + view))), 48.0);
        float sheen = pow(1.0 - abs(dot(normal, view)), 3.0);
        color = color * (1.0 - 0.12 * vGloss) + pc.diffuse.rgb * vGloss * (0.75 * highlight + 0.18 * sheen);
    }
    if (pc.lightAndPass.w > 2.5) {
        // Glare source (the exe's giTechGlareDif/Spe/Emi/Lit): the glare meshes' own colour at full strength, added
        // up. Like the originals' alpha = 1 output, the opacity is ignored: glints such as mp01's mizu_hikari are nearly
        // invisible in the scene (material alpha 0.002..0.1) and only show through the glare.
        // 0 = lit diffuse, 1 = specular (none here), 2 = emissive, 3 = lit.
        int technique = int(pc.lightAndPass.w - 2.5);
        vec3 glare = technique == 2 ? pc.emissive.rgb * texel.rgb : technique == 1 ? vec3(0.0) : clamp(color, 0.0, 1.0);
        outColor = vec4(glare, 1.0);
        return;
    }
    outColor = vec4(color, alpha);
}
