// PMX 2.0/2.1 data model. Raw values as stored in the file (no coordinate
// conversion). Coordinate/winding conversion to Unity space happens later, at
// GameObject construction (M1b), so this layer stays a faithful decode.
//
// Part of the MateEngine PMX offline import pipeline. See Docs/DECISIONS_RECORD.md ADR-0008.
using System.Collections.Generic;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public enum PmxWeightType { BDEF1 = 0, BDEF2 = 1, BDEF4 = 2, SDEF = 3, QDEF = 4 }

    public class PmxVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Uv;
        public Vector4[] AdditionalUv;            // length == PmxModel.AdditionalUvCount
        public PmxWeightType WeightType;
        public readonly int[] BoneIndices = { -1, -1, -1, -1 };
        public readonly float[] BoneWeights = { 0f, 0f, 0f, 0f };
        public Vector3 SdefC, SdefR0, SdefR1;     // only meaningful when WeightType == SDEF
        public float EdgeScale;
    }

    public class PmxMaterial
    {
        public string NameLocal, NameUniversal;
        public Color Diffuse;                     // RGBA
        public Vector3 Specular;
        public float SpecularStrength;
        public Vector3 Ambient;
        public byte DrawFlags;                    // bit0 no-cull, bit1 ground shadow, bit4 edge ...
        public Color EdgeColor;                   // RGBA
        public float EdgeScale;
        public int TextureIndex;                  // -1 == none
        public int EnvironmentIndex;              // sphere/matcap texture, -1 == none
        public byte EnvironmentBlendMode;         // 0 disabled, 1 multiply, 2 additive, 3 additional-vec4
        public byte ToonReference;                // 0 = texture index, 1 = internal toon 0..9
        public int ToonIndex;
        public string Memo;
        public int SurfaceCount;                  // number of vertex indices this material covers
    }

    [System.Flags]
    public enum PmxBoneFlags
    {
        IndexedTail = 0x0001,
        Rotatable = 0x0002,
        Translatable = 0x0004,
        Visible = 0x0008,
        Enabled = 0x0010,
        IK = 0x0020,
        InheritRotation = 0x0100,
        InheritTranslation = 0x0200,
        FixedAxis = 0x0400,
        LocalCoordinate = 0x0800,
        PhysicsAfterDeform = 0x1000,
        ExternalParentDeform = 0x2000,
    }

    public class PmxIkLink
    {
        public int BoneIndex;
        public bool HasLimits;
        public Vector3 LimitMin, LimitMax;        // radians
    }

    public class PmxBone
    {
        public string NameLocal, NameUniversal;
        public Vector3 Position;
        public int ParentIndex = -1;
        public int Layer;
        public int Flags;

        public Vector3 TailOffset;                // when !IndexedTail
        public int TailBoneIndex = -1;            // when IndexedTail

        public int InheritParentIndex = -1;
        public float InheritWeight;

        public Vector3 FixedAxis;
        public Vector3 LocalAxisX, LocalAxisZ;
        public int ExternalParentKey;

        // IK (when Has(IK))
        public int IkTargetBone = -1;
        public int IkLoopCount;
        public float IkLimitRadian;
        public readonly List<PmxIkLink> IkLinks = new();

        public bool Has(PmxBoneFlags f) => (Flags & (int)f) != 0;
    }

    public enum PmxMorphType
    {
        Group = 0, Vertex = 1, Bone = 2,
        Uv = 3, Uv1 = 4, Uv2 = 5, Uv3 = 6, Uv4 = 7,
        Material = 8, Flip = 9, Impulse = 10
    }

    public struct PmxVertexMorphOffset
    {
        public int VertexIndex;
        public Vector3 Translation;
    }

    public class PmxMorph
    {
        public string NameLocal, NameUniversal;
        public byte Panel;                        // 1 eyebrow, 2 eye, 3 mouth, 4 other
        public PmxMorphType Type;
        public int RawOffsetCount;
        // Only vertex morphs are materialized into Unity blendshapes; populated for Type == Vertex.
        public List<PmxVertexMorphOffset> VertexOffsets;
    }

    public class PmxRigidBody
    {
        public string NameLocal, NameUniversal;
        public int BoneIndex = -1;
        public byte Group;
        public ushort NonCollisionMask;
        public byte Shape;                        // 0 sphere, 1 box, 2 capsule
        public Vector3 Size;
        public Vector3 Position;
        public Vector3 Rotation;                  // radians
        public float Mass;
        public float LinearDamping, AngularDamping, Restitution, Friction;
        public byte PhysicsMode;                  // 0 follow-bone (static), 1 physics (dynamic), 2 physics+bone
    }

    public class PmxJoint
    {
        public string NameLocal, NameUniversal;
        public byte Type;                         // 0 = spring 6DOF (only type in 2.0)
        public int RigidBodyA = -1, RigidBodyB = -1;
        public Vector3 Position, Rotation;
        public Vector3 PosLimitLower, PosLimitUpper;
        public Vector3 RotLimitLower, RotLimitUpper;
        public Vector3 PosSpring, RotSpring;
    }

    public class PmxModel
    {
        public float Version;
        public int TextEncoding;                  // 0 UTF-16LE, 1 UTF-8
        public int AdditionalUvCount;

        public string NameLocal, NameUniversal, CommentLocal, CommentUniversal;

        public readonly List<PmxVertex> Vertices = new();
        public readonly List<int> Indices = new();      // triangle list, raw winding
        public readonly List<string> TexturePaths = new();
        public readonly List<PmxMaterial> Materials = new();
        public readonly List<PmxBone> Bones = new();
        public readonly List<PmxMorph> Morphs = new();
        public readonly List<PmxRigidBody> RigidBodies = new();
        public readonly List<PmxJoint> Joints = new();
    }
}
