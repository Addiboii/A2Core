namespace Aion.Core.Characters;

/// <summary>
/// Decodes the stored character customization bytes (the client's _customizing_data, kept
/// verbatim in game.characters.appearance). Layout: one version byte, then one byte per
/// field of EAionPCCustomizeData_V{version} in enum order (usmap). Verified for version 12
/// (329 bytes). Sliders centre on 100; *Type fields store table index + 100, where index 1
/// is "None"; C_*_R/G/B are colour channels 0-255. Mapping indices to meshes/colours needs
/// the client's PcCustomizeFeatureTable_Material (see tools/wardrobe.py).
/// </summary>
public sealed record CharacterAppearance(int Version, IReadOnlyDictionary<string, int> Fields)
{
    /// <summary>EAionPCCustomizeData_V12 without the trailing Max entry.</summary>
    public static readonly IReadOnlyList<string> V12Fields =
    [
        "Length_Waist", "Length_Spine", "Length_Thigh", "Length_Calf", "Length_UpperArm", "Length_LowerArm",
        "Thick_AllBody", "Size_Head", "Thick_Neck", "Length_Neck", "Size_Spine", "Size_Breast", "Pos_LR_Breast",
        "Pos_UpDown_Breast", "UpVolume_Breast", "Width_Waist", "Size_Abdominal", "Width_Shoulder", "Size_Trapezius",
        "Size_Lat", "Width_Pelvis", "UpDown_Butt", "Size_Butt", "Thick_Leg", "Thick_Quadriceps", "Thick_CalfMuscle",
        "Width_Foot", "Size_Deltoid", "Size_Triceps", "Size_Biceps", "Thick_Upperarm", "Thick_Forearm",
        "Thick_Arm", "Size_Hand", "Thick_Finger", "Length_Finger", "Height", "Vein", "Velly_Intensity",
        "Arm_Intensity", "Leg_Intensity", "BMI", "ForeHead_Height", "CheekBone_Height", "AppleZone_Height",
        "CheekBone_UpDown", "Chin_Length", "Chin_Width", "Chin_Forward", "Plastima_UpDown", "Plastima_Width",
        "PlastimaA_UpDown", "Shape_UpDown", "Shape_InOut", "Puff_InOut", "EyeBrow_UpDown", "EyeBrow_Depth",
        "EyeBrowIn_InOut", "Eye_UpDown", "Eye_Dist", "Eye_Depth", "Eye_Rotate", "Eye_Size", "EyeTail_InOut",
        "EyeTail_UpDown", "EyeHead_UpDown", "EyeHead_InOut", "EyeBrowUpper_UpDown", "EyeBrowLower_UpDown",
        "Nose_Size", "Nose_Length", "NoseTip_Height", "NoseTip_UpDown", "NoseBridge_Height", "NoseBridge_UpDown",
        "NoseBridgeLow_Height", "NoseBridgeLow_UpDown", "Nostril_InOut", "Nostril_UpDown", "Lip_UpDown",
        "Lip_Width", "Lip_Thickness", "LipCorner_UpDown", "LipCorner_InOut", "LipCorner_Depth", "LipLower_Width",
        "LipLower_Thickness", "LipUpper_Width", "LipUpper_Thickness", "LipUpperSide_Thickness", "Lip_Height",
        "LipLower_Height", "LipUpper_Height", "LipRotation", "Ear_UpDown", "Ear_Size", "Ear_FwdBwd",
        "Ear_Rotate", "Earlobe_Size", "EarShapeChange", "EyelashLength", "EyelashUpDown", "SkinColor",
        "FaceGlossiness", "BodyGlossiness", "bUseBodyGlossiness", "bOddEye_Pupil", "PupilSize_L", "PupilSize_R",
        "PupilShape_L", "PupilShape_R", "C_PupilColor_L_R", "C_PupilColor_L_G", "C_PupilColor_L_B", "C_PupilColor_R_R",
        "C_PupilColor_R_G", "C_PupilColor_R_B", "bUseEmiPupil_L", "bUseEmiPupil_R", "bOddEye_Iris", "IrisSize_L",
        "IrisSize_R", "IrisShape_L", "IrisShape_R", "C_IrisColorMid_L_R", "C_IrisColorMid_L_G", "C_IrisColorMid_L_B",
        "C_IrisColorEdge_L_R", "C_IrisColorEdge_L_G", "C_IrisColorEdge_L_B", "C_IrisColorMid_R_R", "C_IrisColorMid_R_G",
        "C_IrisColorMid_R_B", "C_IrisColorEdge_R_R", "C_IrisColorEdge_R_G", "C_IrisColorEdge_R_B", "bOddEye_Sclera",
        "C_ScleraColor_L_R", "C_ScleraColor_L_G", "C_ScleraColor_L_B", "C_ScleraColor_R_R", "C_ScleraColor_R_G",
        "C_ScleraColor_R_B", "ScleraVeinsPower_L", "ScleraVeinsPower_R", "C_ScleraVeinColor_L_R", "C_ScleraVeinColor_L_G",
        "C_ScleraVeinColor_L_B", "C_ScleraVeinColor_R_R", "C_ScleraVeinColor_R_G", "C_ScleraVeinColor_R_B",
        "EyelashType", "C_EyelashColor_Upper_R", "C_EyelashColor_Upper_G", "C_EyelashColor_Upper_B",
        "C_EyelashColor_Lower_R", "C_EyelashColor_Lower_G", "C_EyelashColor_Lower_B", "EyelashThickness_Upper",
        "EyelashThickness_Lower", "EyebrowType", "C_EyebrowColor_R", "C_EyebrowColor_G", "C_EyebrowColor_B",
        "EyebrowHairIntensity", "EyebrowRoughness", "EyebrowUpDown", "EyebrowDist", "EyebrowRotate",
        "EyebrowWidth", "EyebrowThickness", "ShadingType", "C_ShadingColor_R", "C_ShadingColor_G", "C_ShadingColor_B",
        "ShadingAlpha", "ShadingRoughness", "HighlighterType", "C_HighlighterColor_R", "C_HighlighterColor_G",
        "C_HighlighterColor_B", "HighlighterAlpha", "HighlighterMetalic", "HighlighterRoughness", "BlusherType",
        "C_BlusherColor_R", "C_BlusherColor_G", "C_BlusherColor_B", "BlusherAlpha", "BlusherRoughness",
        "GlitterArea_Blusher", "GlitterColorUse_Blusher", "C_GlitterColor_Blusher_R", "C_GlitterColor_Blusher_G",
        "C_GlitterColor_Blusher_B", "GlitterMetalic_Blusher", "GlitterSize_Blusher", "GlitterDensity_Blusher",
        "EyeshadowType", "C_EyeshadowColor_R", "C_EyeshadowColor_G", "C_EyeshadowColor_B", "EyeshadowAlpha",
        "EyeshadowMetalic", "EyeshadowRoughness", "Eyeshadow2Type", "C_Eyeshadow2Color_R", "C_Eyeshadow2Color_G",
        "C_Eyeshadow2Color_B", "Eyeshadow2Alpha", "Eyeshadow2Metalic", "Eyeshadow2Roughness", "GlitterArea_EyeShadowA",
        "GlitterColorUse_EyeShadowA", "C_GlitterColor_EyeShadowA_R", "C_GlitterColor_EyeShadowA_G", "C_GlitterColor_EyeShadowA_B",
        "GlitterMetalic_EyeShadowA", "GlitterSize_EyeShadowA", "GlitterDensity_EyeShadowA", "GlitterArea_EyeShadowB",
        "GlitterColorUse_EyeShadowB", "C_GlitterColor_EyeShadowB_R", "C_GlitterColor_EyeShadowB_G", "C_GlitterColor_EyeShadowB_B",
        "GlitterMetalic_EyeShadowB", "GlitterSize_EyeShadowB", "GlitterDensity_EyeShadowB", "EyelineType",
        "C_EyelineColor_R", "C_EyelineColor_G", "C_EyelineColor_B", "EyelineAlpha", "EyelineMetalic",
        "EyelineRoughness", "LipType", "C_LipColor_R", "C_LipColor_G", "C_LipColor_B", "LipAlpha", "LipMetalic",
        "LipRoughness", "Lip2Type", "C_Lip2Color_R", "C_Lip2Color_G", "C_Lip2Color_B", "Lip2Alpha", "Lip2Metalic",
        "Lip2Roughness", "GlitterArea_Lip", "GlitterColorUse_Lip", "C_GlitterColor_Lip_R", "C_GlitterColor_Lip_G",
        "C_GlitterColor_Lip_B", "GlitterMetalic_Lip", "GlitterSize_Lip", "GlitterDensity_Lip", "FreckleType",
        "C_FreckleColor_R", "C_FreckleColor_G", "C_FreckleColor_B", "FreckleAlpha", "MoleType", "C_MoleColor_R",
        "C_MoleColor_G", "C_MoleColor_B", "MoleAlpha", "MoleUpDown", "MoleLeftRight", "TattooType", "C_TattooColor_R",
        "C_TattooColor_G", "C_TattooColor_B", "TattooUpDown", "TattooLeftRight", "TattooScaleU", "TattooScaleV",
        "TattooRotate", "bTattooFlipU", "bTattooFlipV", "bTattooSymmetry", "ScarType", "C_ScarColor_R",
        "C_ScarColor_G", "C_ScarColor_B", "ScarUpDown", "ScarLeftRight", "ScarScaleU", "ScarScaleV",
        "ScarRotate", "ScarDepth", "WarPaintType", "C_WarPaintR_Color_R", "C_WarPaintR_Color_G", "C_WarPaintR_Color_B",
        "WarPaintR_NormalInt", "WarPaintR_Metallic", "WarPaintR_Roughness", "C_WarPaintG_Color_R", "C_WarPaintG_Color_G",
        "C_WarPaintG_Color_B", "WarPaintG_NormalInt", "WarPaintG_Metallic", "WarPaintG_Roughness", "BodyTattooType_Torso_F",
        "BodyTattooType_Torso_B", "BodyTattooType_Arm_R", "BodyTattooType_Arm_L", "BodyTattooType_Leg_R",
        "BodyTattooType_Leg_L", "HeadType", "HairType", "C_HairColor_R", "C_HairColor_G", "C_HairColor_B",
        "C_Hair2Color_R", "C_Hair2Color_G", "C_Hair2Color_B", "HairGlossiness", "bUseHairColorDetailSettings",
        "RootTipRange", "RootTipContrast", "BeardType", "C_BeardColor_R", "C_BeardColor_G", "C_BeardColor_B",
        "StubbleType", "StubbleIntensity",
    ];

    public static CharacterAppearance Decode(byte[] data)
    {
        if (data.Length == 0) return new(0, new Dictionary<string, int>());
        var version = data[0];
        if (version != 12 || data.Length != V12Fields.Count + 1) return new(version, new Dictionary<string, int>());
        var fields = new Dictionary<string, int>(V12Fields.Count);
        for (var i = 0; i < V12Fields.Count; i++) fields[V12Fields[i]] = data[i + 1];
        return new(version, fields);
    }

    /// <summary>Table index of a *Type field (stored value - 100), or null when absent.</summary>
    public int? TypeIndex(string field) => Fields.TryGetValue(field, out var value) ? value - 100 : null;

    /// <summary>"#rrggbb" from C_{name}_R/G/B, or null when absent.</summary>
    public string? Color(string name) =>
        Fields.TryGetValue($"C_{name}_R", out var r) && Fields.TryGetValue($"C_{name}_G", out var g)
            && Fields.TryGetValue($"C_{name}_B", out var b) ? $"#{r:x2}{g:x2}{b:x2}" : null;
}
