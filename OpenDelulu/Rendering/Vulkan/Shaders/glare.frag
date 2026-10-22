#version 450
// The post passes of the original's gi_dx9glare (the DirectX SDK star & bloom): shrink, Gaussian bloom, star lines
// and the final add. One shader, the pass is picked by params.w.
layout(set = 0, binding = 0) uniform sampler2D uSource;

layout(push_constant) uniform Push
{
    vec4 texel;   // xy = source texel size, zw = direction (blur) or step (star), in texture coordinates
    vec4 params;  // mode-specific, w = mode
} pc;

layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

// CStarDef's chromatic aberration colours, as pushed by the exe.
const vec3 CHROMATIC[8] = vec3[8](
    vec3(0.5, 0.5, 0.5), vec3(0.8, 0.3, 0.3), vec3(1.0, 0.2, 0.2), vec3(0.5, 0.2, 0.6),
    vec3(0.2, 0.2, 1.0), vec3(0.2, 0.3, 0.7), vec3(0.2, 0.6, 0.2), vec3(0.3, 0.5, 0.3));

void main()
{
    float mode = pc.params.w;
    if (mode < 0.5)
    {
        // Shrink to a quarter: average of 4x4 source texels.
        vec3 sum = vec3(0.0);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                sum += texture(uSource, vUV + (vec2(x, y) * 2.0 - 1.0) * pc.texel.xy).rgb;
        outColor = vec4(sum / 4.0, 1.0);
    }
    else if (mode < 1.5)
    {
        // GetSampleOffsets_Bloom: 15 taps, weight = multiplier (params.y) x Gaussian(i, deviation params.x).
        float deviation = max(pc.params.x, 0.001);
        vec3 sum = vec3(0.0);
        for (int i = -7; i <= 7; i++)
        {
            float g = exp(-float(i * i) / (2.0 * deviation * deviation)) / sqrt(6.2831853 * deviation * deviation);
            sum += texture(uSource, vUV + float(i) * pc.texel.xy * pc.texel.zw).rgb * g;
        }
        outColor = vec4(sum * pc.params.y, 1.0);
    }
    else if (mode < 2.5)
    {
        // RenderStar: 8 samples along the line, weight = colour x 0.95^(attnPowScale (params.x) x s) x (pass + 1) / 2
        // (params.y); samples 0.9 or more away are dropped.
        vec3 sum = vec3(0.0);
        for (int s = 0; s < 8; s++)
        {
            vec2 offset = float(s) * pc.texel.zw;
            if (abs(offset.x) >= 0.9 || abs(offset.y) >= 0.9)
                continue;
            vec3 aberration = mix(CHROMATIC[s], vec3(0.63), float(s + 1) / 8.0);
            vec3 color = mix(aberration, vec3(1.0), 0.7);
            float lum = pow(0.95, pc.params.x * float(s));
            sum += texture(uSource, vUV + offset).rgb * color * lum * pc.params.y;
        }
        outColor = vec4(sum, 1.0);
    }
    else
    {
        // The final add (blended One/One onto the frame): the glare times params.x.
        outColor = vec4(texture(uSource, vUV).rgb * pc.params.x, 1.0);
    }
}
