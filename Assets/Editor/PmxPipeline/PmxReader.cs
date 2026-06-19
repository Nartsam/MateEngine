// Binary parser for PMX 2.0 / 2.1 files. Faithful decode into PmxModel; no
// coordinate conversion here. Reference: PMX 2.0/2.1 format specification.
//
// Part of the MateEngine PMX offline import pipeline. See Docs/DECISIONS_RECORD.md ADR-0008.
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public class PmxParseException : Exception
    {
        public PmxParseException(string message) : base(message) { }
    }

    public class PmxReader
    {
        private BinaryReader _r;

        // Globals (byte widths / flags from header).
        private int _encoding;        // 0 UTF-16LE, 1 UTF-8
        private int _addUv;
        private int _vIdx, _texIdx, _matIdx, _boneIdx, _morphIdx, _rbIdx;

        public PmxModel ReadFile(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            _r = br;

            var m = new PmxModel();
            ReadHeader(m);
            ReadModelInfo(m);
            ReadVertices(m);
            ReadFaces(m);
            ReadTextures(m);
            ReadMaterials(m);
            ReadBones(m);
            ReadMorphs(m);
            ReadDisplayFrames();   // parsed and discarded (not needed downstream)
            ReadRigidBodies(m);
            ReadJoints(m);
            // 2.1 soft bodies, if present, are intentionally ignored.
            return m;
        }

        // ---- primitives -------------------------------------------------------

        private Vector2 Vec2() => new(_r.ReadSingle(), _r.ReadSingle());
        private Vector3 Vec3() => new(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle());
        private Vector4 Vec4() => new(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle());

        private Color Color4()
        {
            float r = _r.ReadSingle(), g = _r.ReadSingle(), b = _r.ReadSingle(), a = _r.ReadSingle();
            return new Color(r, g, b, a);
        }

        private string Text()
        {
            int len = _r.ReadInt32();
            if (len < 0) throw new PmxParseException($"Negative text length {len}");
            if (len == 0) return string.Empty;
            byte[] bytes = _r.ReadBytes(len);
            return _encoding == 0 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }

        // Reference indices are signed (-1 == none).
        private int Index(int size) => size switch
        {
            1 => _r.ReadSByte(),
            2 => _r.ReadInt16(),
            4 => _r.ReadInt32(),
            _ => throw new PmxParseException($"Bad index size {size}")
        };

        // Vertex indices (in faces / vertex & UV morphs) are unsigned.
        private int VertexIndex() => _vIdx switch
        {
            1 => _r.ReadByte(),
            2 => _r.ReadUInt16(),
            4 => _r.ReadInt32(),
            _ => throw new PmxParseException($"Bad vertex index size {_vIdx}")
        };

        // ---- sections ---------------------------------------------------------

        private void ReadHeader(PmxModel m)
        {
            var sig = new string(_r.ReadChars(4));
            if (sig != "PMX " && sig != "Pmx ")
                throw new PmxParseException($"Not a PMX file (signature '{sig}')");

            m.Version = _r.ReadSingle();
            if (m.Version < 2.0f)
                throw new PmxParseException($"Unsupported PMX version {m.Version} (need 2.0+)");

            int globalsCount = _r.ReadByte();
            byte[] g = _r.ReadBytes(globalsCount);
            if (globalsCount < 8)
                throw new PmxParseException($"Too few globals ({globalsCount})");

            _encoding = g[0];
            _addUv = g[1];
            _vIdx = g[2];
            _texIdx = g[3];
            _matIdx = g[4];
            _boneIdx = g[5];
            _morphIdx = g[6];
            _rbIdx = g[7];

            m.TextEncoding = _encoding;
            m.AdditionalUvCount = _addUv;
        }

        private void ReadModelInfo(PmxModel m)
        {
            m.NameLocal = Text();
            m.NameUniversal = Text();
            m.CommentLocal = Text();
            m.CommentUniversal = Text();
        }

        private void ReadVertices(PmxModel m)
        {
            int count = _r.ReadInt32();
            m.Vertices.Capacity = count;
            for (int i = 0; i < count; i++)
            {
                var v = new PmxVertex
                {
                    Position = Vec3(),
                    Normal = Vec3(),
                    Uv = Vec2()
                };
                if (_addUv > 0)
                {
                    v.AdditionalUv = new Vector4[_addUv];
                    for (int k = 0; k < _addUv; k++) v.AdditionalUv[k] = Vec4();
                }

                v.WeightType = (PmxWeightType)_r.ReadByte();
                switch (v.WeightType)
                {
                    case PmxWeightType.BDEF1:
                        v.BoneIndices[0] = Index(_boneIdx);
                        v.BoneWeights[0] = 1f;
                        break;
                    case PmxWeightType.BDEF2:
                        v.BoneIndices[0] = Index(_boneIdx);
                        v.BoneIndices[1] = Index(_boneIdx);
                        v.BoneWeights[0] = _r.ReadSingle();
                        v.BoneWeights[1] = 1f - v.BoneWeights[0];
                        break;
                    case PmxWeightType.BDEF4:
                    case PmxWeightType.QDEF:
                        v.BoneIndices[0] = Index(_boneIdx);
                        v.BoneIndices[1] = Index(_boneIdx);
                        v.BoneIndices[2] = Index(_boneIdx);
                        v.BoneIndices[3] = Index(_boneIdx);
                        v.BoneWeights[0] = _r.ReadSingle();
                        v.BoneWeights[1] = _r.ReadSingle();
                        v.BoneWeights[2] = _r.ReadSingle();
                        v.BoneWeights[3] = _r.ReadSingle();
                        break;
                    case PmxWeightType.SDEF:
                        v.BoneIndices[0] = Index(_boneIdx);
                        v.BoneIndices[1] = Index(_boneIdx);
                        v.BoneWeights[0] = _r.ReadSingle();
                        v.BoneWeights[1] = 1f - v.BoneWeights[0];
                        v.SdefC = Vec3();
                        v.SdefR0 = Vec3();
                        v.SdefR1 = Vec3();
                        break;
                    default:
                        throw new PmxParseException($"Unknown weight type {(int)v.WeightType} at vertex {i}");
                }

                v.EdgeScale = _r.ReadSingle();
                m.Vertices.Add(v);
            }
        }

        private void ReadFaces(PmxModel m)
        {
            int indexCount = _r.ReadInt32();
            m.Indices.Capacity = indexCount;
            for (int i = 0; i < indexCount; i++)
                m.Indices.Add(VertexIndex());
        }

        private void ReadTextures(PmxModel m)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
                m.TexturePaths.Add(Text());
        }

        private void ReadMaterials(PmxModel m)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var mat = new PmxMaterial
                {
                    NameLocal = Text(),
                    NameUniversal = Text(),
                    Diffuse = Color4(),
                    Specular = Vec3(),
                    SpecularStrength = _r.ReadSingle(),
                    Ambient = Vec3(),
                    DrawFlags = _r.ReadByte(),
                    EdgeColor = Color4(),
                    EdgeScale = _r.ReadSingle(),
                    TextureIndex = Index(_texIdx),
                    EnvironmentIndex = Index(_texIdx),
                    EnvironmentBlendMode = _r.ReadByte(),
                    ToonReference = _r.ReadByte()
                };
                mat.ToonIndex = mat.ToonReference == 0 ? Index(_texIdx) : _r.ReadByte();
                mat.Memo = Text();
                mat.SurfaceCount = _r.ReadInt32();
                m.Materials.Add(mat);
            }
        }

        private void ReadBones(PmxModel m)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var b = new PmxBone
                {
                    NameLocal = Text(),
                    NameUniversal = Text(),
                    Position = Vec3(),
                    ParentIndex = Index(_boneIdx),
                    Layer = _r.ReadInt32(),
                    Flags = _r.ReadUInt16()
                };

                if (b.Has(PmxBoneFlags.IndexedTail)) b.TailBoneIndex = Index(_boneIdx);
                else b.TailOffset = Vec3();

                if (b.Has(PmxBoneFlags.InheritRotation) || b.Has(PmxBoneFlags.InheritTranslation))
                {
                    b.InheritParentIndex = Index(_boneIdx);
                    b.InheritWeight = _r.ReadSingle();
                }
                if (b.Has(PmxBoneFlags.FixedAxis)) b.FixedAxis = Vec3();
                if (b.Has(PmxBoneFlags.LocalCoordinate)) { b.LocalAxisX = Vec3(); b.LocalAxisZ = Vec3(); }
                if (b.Has(PmxBoneFlags.ExternalParentDeform)) b.ExternalParentKey = _r.ReadInt32();

                if (b.Has(PmxBoneFlags.IK))
                {
                    b.IkTargetBone = Index(_boneIdx);
                    b.IkLoopCount = _r.ReadInt32();
                    b.IkLimitRadian = _r.ReadSingle();
                    int linkCount = _r.ReadInt32();
                    for (int l = 0; l < linkCount; l++)
                    {
                        var link = new PmxIkLink { BoneIndex = Index(_boneIdx), HasLimits = _r.ReadByte() != 0 };
                        if (link.HasLimits) { link.LimitMin = Vec3(); link.LimitMax = Vec3(); }
                        b.IkLinks.Add(link);
                    }
                }

                m.Bones.Add(b);
            }
        }

        private void ReadMorphs(PmxModel m)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var morph = new PmxMorph
                {
                    NameLocal = Text(),
                    NameUniversal = Text(),
                    Panel = _r.ReadByte(),
                    Type = (PmxMorphType)_r.ReadByte()
                };
                int offsetCount = _r.ReadInt32();
                morph.RawOffsetCount = offsetCount;

                switch (morph.Type)
                {
                    case PmxMorphType.Group:
                    case PmxMorphType.Flip:
                        for (int o = 0; o < offsetCount; o++) { Index(_morphIdx); _r.ReadSingle(); }
                        break;
                    case PmxMorphType.Vertex:
                        morph.VertexOffsets = new System.Collections.Generic.List<PmxVertexMorphOffset>(offsetCount);
                        for (int o = 0; o < offsetCount; o++)
                            morph.VertexOffsets.Add(new PmxVertexMorphOffset { VertexIndex = VertexIndex(), Translation = Vec3() });
                        break;
                    case PmxMorphType.Bone:
                        for (int o = 0; o < offsetCount; o++) { Index(_boneIdx); Vec3(); Vec4(); }
                        break;
                    case PmxMorphType.Uv:
                    case PmxMorphType.Uv1:
                    case PmxMorphType.Uv2:
                    case PmxMorphType.Uv3:
                    case PmxMorphType.Uv4:
                        for (int o = 0; o < offsetCount; o++) { VertexIndex(); Vec4(); }
                        break;
                    case PmxMorphType.Material:
                        // matIndex + op(1) + diffuse(4) + specular(3) + specStr(1) + ambient(3)
                        // + edge(4) + edgeScale(1) + texTint(4) + envTint(4) + toonTint(4)
                        for (int o = 0; o < offsetCount; o++)
                        {
                            Index(_matIdx); _r.ReadByte();
                            Vec4(); Vec3(); _r.ReadSingle(); Vec3();
                            Vec4(); _r.ReadSingle(); Vec4(); Vec4(); Vec4();
                        }
                        break;
                    case PmxMorphType.Impulse:
                        for (int o = 0; o < offsetCount; o++) { Index(_rbIdx); _r.ReadByte(); Vec3(); Vec3(); }
                        break;
                    default:
                        throw new PmxParseException($"Unknown morph type {(int)morph.Type} ('{morph.NameLocal}')");
                }

                m.Morphs.Add(morph);
            }
        }

        private void ReadDisplayFrames()
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                Text(); Text();        // name local / universal
                _r.ReadByte();         // special flag
                int elements = _r.ReadInt32();
                for (int e = 0; e < elements; e++)
                {
                    int target = _r.ReadByte();          // 0 bone, 1 morph
                    Index(target == 1 ? _morphIdx : _boneIdx);
                }
            }
        }

        private void ReadRigidBodies(PmxModel m)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                m.RigidBodies.Add(new PmxRigidBody
                {
                    NameLocal = Text(),
                    NameUniversal = Text(),
                    BoneIndex = Index(_boneIdx),
                    Group = _r.ReadByte(),
                    NonCollisionMask = _r.ReadUInt16(),
                    Shape = _r.ReadByte(),
                    Size = Vec3(),
                    Position = Vec3(),
                    Rotation = Vec3(),
                    Mass = _r.ReadSingle(),
                    LinearDamping = _r.ReadSingle(),
                    AngularDamping = _r.ReadSingle(),
                    Restitution = _r.ReadSingle(),
                    Friction = _r.ReadSingle(),
                    PhysicsMode = _r.ReadByte()
                });
            }
        }

        private void ReadJoints(PmxModel m)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                m.Joints.Add(new PmxJoint
                {
                    NameLocal = Text(),
                    NameUniversal = Text(),
                    Type = _r.ReadByte(),
                    RigidBodyA = Index(_rbIdx),
                    RigidBodyB = Index(_rbIdx),
                    Position = Vec3(),
                    Rotation = Vec3(),
                    PosLimitLower = Vec3(),
                    PosLimitUpper = Vec3(),
                    RotLimitLower = Vec3(),
                    RotLimitUpper = Vec3(),
                    PosSpring = Vec3(),
                    RotSpring = Vec3()
                });
            }
        }
    }
}
