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
    float lambert = abs(dot(normalize(vNormal), pc.lightAndPass.xyz));
    vec3 lit = pc.emissive.rgb + pc.ambient.rgb * 0.35 + pc.diffuse.rgb * 0.55 * lambert;
    outColor = vec4(texel.rgb * min(lit, vec3(1.15)), alpha);
}
