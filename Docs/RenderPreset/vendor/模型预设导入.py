bl_info = {
    "name": "模型预设导入",
    "description" : "",
    "author": "",
    "version": (0,  0, 1),
    "blender": (3, 6, 0),
    "location": "View3D UI",
    "category": "Import-Export"
}

# ------------------------------------------------------------------

import bpy
import os
from bpy_extras.io_utils import ImportHelper
from bpy.props import CollectionProperty, StringProperty, BoolProperty, EnumProperty
from difflib import SequenceMatcher #导入模糊匹配库

# ------------------------------------------------------------------

# 模糊搜索函数
def similarity(a, b):
    return SequenceMatcher(None, a, b).ratio()

# 颜色编码转换函数
def srgb_to_linearrgb(c):
    if   c < 0:       return 0
    elif c < 0.04045: return c/12.92
    else:             return ((c+0.055)/1.055)**2.4

def hex_to_rgb(h):
    r = (h & 0xff0000) >> 16
    g = (h & 0x00ff00) >> 8
    b = (h & 0x0000ff)
    return tuple([srgb_to_linearrgb(c/0xff) for c in (r,g,b)])


# ------------------------------------------------------------------

def load_presets(filepath, objects_name, hbone_name, fuzzy_matching, set_scene_effect):
    with bpy.data.libraries.load(filepath) as (data_from, data_to):
        objects_data_name = data_from.objects

    for ob in bpy.data.objects:
        if ob.name in objects_data_name:
            ob.name = "actual_" + ob.name

    # ------------------------------------------------------------------

    with bpy.data.libraries.load(filepath) as (data_from, data_to):
        if fuzzy_matching == 2:
            materials_data_name = [m.rsplit(".",1)[0] for m in data_from.materials]
        else:
            materials_data_name = data_from.materials
                
    if bpy.context.object:
        object = bpy.context.object
        if object.type == 'MESH':
            if fuzzy_matching in [1,2]:
                for m in bpy.data.materials:
                    if m.name in materials_data_name:
                        m.name = "actual_" + m.name
            for s in bpy.context.object.material_slots:
                s.material.name = s.name.replace('actual_','',1)
                name = s.name
                if fuzzy_matching == 2:
                    name = name.rsplit(".",1)[0]
                if fuzzy_matching in [0,2]:
                    if name in materials_data_name:
                        if s.name == name:
                            s.material.name = "actual_" + s.name
                        with bpy.data.libraries.load(filepath) as (data_from, data_to):
                            data_to.materials = [name]
                        s.material = bpy.data.materials[name]
                        
                elif fuzzy_matching == 1:
                    matching_name = [.0,""]
                    for name in materials_data_name:
                        ratio = similarity(s.name,name)
                        if ratio > matching_name[0]:
                            matching_name = [ratio,name]
                            
                    if matching_name[0] > .0:
                        if s.name == matching_name[1]:
                            s.material.name = "actual_" + s.name
                        with bpy.data.libraries.load(filepath) as (data_from, data_to):
                            data_to.materials = [matching_name[1]]
                        s.material = bpy.data.materials[matching_name[1]]
                        
    # ------------------------------------------------------------------

    with bpy.data.libraries.load(filepath) as (data_from, data_to):
        node_groups_data_name = data_from.node_groups

    for n in bpy.data.node_groups:
        if n.name in node_groups_data_name:
            n.name = "actual_" + n.name

    with bpy.data.libraries.load(filepath) as (data_from, data_to):
         data_to.node_groups = data_from.node_groups

    for n in node_groups_data_name:
        node_group = bpy.data.node_groups[n]
        if node_group.type == 'GEOMETRY' and node_group.users == 0:
            
            for o in node_group.outputs:
                if o.default_attribute_name == "":
                    o.default_attribute_name = o.name
            if object.type == 'MESH':
                modifier = bpy.context.object.modifiers.new(n,'NODES')
                modifier.node_group = node_group

    # ------------------------------------------------------------------
    with bpy.data.libraries.load(filepath) as (data_from, data_to):
         data_to.objects = data_from.objects

    for ob in objects_data_name:
        bpy.context.scene.collection.objects.link(bpy.data.objects[ob])
        if ob == objects_name :
            for m in bpy.context.object.modifiers:
                if m.type == 'ARMATURE':
                    if m.object:
                        Pos_object = bpy.data.objects[ob]
                        Pos_object.parent = m.object
                        Pos_object.parent_type = 'BONE'
                        
                        for bone in m.object.pose.bones:
                            if bone.name == hbone_name:
                                Pos_object.parent_bone = hbone_name
                                Rot = bone.matrix @ m.object.data.bones[hbone_name].matrix_local.inverted()
                                Pos_object.matrix_world = m.object.matrix_world @ Rot @ Pos_object.matrix_world
                        
    # ------------------------------------------------------------------
    
    del_matname_list = []
    
    for m in bpy.data.materials:
        if m.library_weak_reference:
            if m.library_weak_reference.filepath == filepath:
                 if m.name not in materials_data_name:
                     del_matname_list.append(m.name)
                     m.user_remap(bpy.data.materials[m.name.rsplit(".",1)[0]])
    
    for name in del_matname_list:
        bpy.data.materials.remove(bpy.data.materials[name])
    
# ------------------------------------------------------------------

    if set_scene_effect:
        bpy.context.scene.eevee.use_bloom = True
        bpy.context.scene.eevee.bloom_intensity = 0.08
        bpy.context.scene.eevee.bloom_color = hex_to_rgb(0xFFE4D9)
        bpy.context.scene.view_settings.view_transform = 'Filmic'
        bpy.context.scene.view_settings.look = 'High Contrast'
        
# ------------------------------------------------------------------

class ImportMatPresets(bpy.types.Operator, ImportHelper):
    """ 选择对应模型预设导入 """
    bl_idname = "import_test.import_mat_presets" 
    bl_label = "导入材质到模型"
    bl_options = {"UNDO"}
    
    files : CollectionProperty(type=bpy.types.PropertyGroup)
    filter_glob: StringProperty(  
        default = "*.blend",
        options = {'HIDDEN'},
        maxlen = 255,  # 最大内部缓冲区长度，越长将被夹紧。
    )

    objects_name: StringProperty(
        name = "面部定位",
        description = "面部定位物体的名称",
        default = "面部定位",
    )
    hbone_name: StringProperty(
        name = "面部骨骼",
        description = "绑定到面部骨骼名称",
        default = "頭",
    )  

    set_scene_effect: BoolProperty(
        name="设置场景效果",
        description="设置EEVEE的必要显示效果",
        default=True,
    )
    
    fuzzy_matching: EnumProperty(
        name = '名称匹配模式',
        description = "选择不同的匹配方法",
        items = (
            ("0", "精确匹配", "只有完全对应的名称才能得到匹配", ),
            ("1", "模糊匹配", "通过模糊搜索名称", ),
            ("2", "剪去后缀", "对现有和导入的材质进行后缀裁剪，然后匹配",),
                ),
        default = "2",
    )

    @classmethod
    def poll(self,context):
        return context.object is not None and context.object.type == 'MESH'
       
    def execute(self, context):
        
        dirname = os.path.dirname(self.filepath)
        filepath = os.path.join(dirname, self.files[0].name) # 从集合指针获取文件路径财产
#        print(str(self.fuzzy_matching))
        if filepath:
            load_presets(filepath, self.objects_name, self.hbone_name, int(self.fuzzy_matching), self.set_scene_effect)
     
        return {'FINISHED'}

# ------------------------------------------------------------------

class ImportMatPresetsUI(bpy.types.Panel):
    
    bl_category = "XiaoerTools"   #侧边栏标签
    bl_label = "import material"     #工具卷展栏标签
    bl_idname = "OBJECT_PT_import"   #工具ID
    bl_space_type = 'VIEW_3D'   #空间类型():3D视图
    bl_region_type = 'UI'       #区域类型:右边侧栏
    
    
    
    # 定义一个绘制函数
    def draw(self, context):    
        row = self.layout.row() #子布局
        row.scale_y=2
        row.operator(ImportMatPresets.bl_idname,text="导入预设")
        
# ------------------------------------------------------------------
    
classes = (
            ImportMatPresets,
            ImportMatPresetsUI,
            )

def register():
    for clss in classes:
        bpy.utils.register_class(clss)

def unregister():
    for clss in classes:
        bpy.utils.unregister_class(clss)

if __name__ == "__main__":
    register()
    