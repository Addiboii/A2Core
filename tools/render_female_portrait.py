"""Blender-only still renderer for the selected local AION2 outfit assets."""
import json
import sys
from pathlib import Path
import bpy
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'
sys.path.insert(0, str(LOCAL / 'tooling/blender-python-deps'))
sys.path.insert(0, str(LOCAL / 'tooling/h4lfheart-UEFormat-d858fe3/plugins/blender'))
from io_scene_ueformat.importer.import_context import UEFormatImport
from io_scene_ueformat.options import UEModelOptions, UEAnimOptions

source = LOCAL / 'exports/female-portrait'
out = LOCAL / 'preview/female'
out.mkdir(parents=True, exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.samples = 48
scene.cycles.use_denoising = True
scene.render.resolution_x = 1200
scene.render.resolution_y = 1500
scene.render.resolution_percentage = 100
scene.world.color = (0.12, 0.12, 0.12)
scene.view_settings.view_transform = 'AgX'

names = ['GF_BaseHead', 'GF_Hair_012'] + ['GF_SpiritKnt_01_' + part for part in ('Body','Boots','Glove','Pants','Shoulder','Cape')]
rigs = {}
for name in names:
    rigs[name] = UEFormatImport(UEModelOptions(import_sockets=False)).import_file(next(source.rglob(name+'.uemodel')))
weapon = UEFormatImport(UEModelOptions(import_sockets=False)).import_file(next(source.rglob('SW_0100_T06.uemodel')))

materials = {p.stem: json.loads(p.read_text()) for p in source.rglob('*.json')}
textures = {p.stem: p for p in source.rglob('*.png')}
mat_report = []
for mat in bpy.data.materials:
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = (0.52, 0.31, 0.23, 1)
    shader.inputs['Roughness'].default_value = 0.48
    props = materials.get(mat.name, {})
    entries = props.get('Textures', {})
    def image_node(key):
        obj = entries.get(key)
        name = obj['ObjectName'].split("'")[1] if obj else None
        if name not in textures or any(x in name for x in ('Mask_Black','Checker','T_Base_M')):
            return None
        n = mat.node_tree.nodes.new('ShaderNodeTexImage')
        n.image = bpy.data.images.load(str(textures[name]), check_existing=True)
        return n
    diffuse = image_node('BaseColor') or image_node('PM_Diffuse')
    if diffuse:
        mat.node_tree.links.new(diffuse.outputs['Color'], shader.inputs['Base Color'])
    if 'SpiritKnt' in mat.name or 'SW_0100' in mat.name:
        shader.inputs['Metallic'].default_value = 0.7
        shader.inputs['Roughness'].default_value = 0.3
        arm = image_node('ARM')
        if arm:
            arm.image.colorspace_settings.name = 'Non-Color'
            separate = mat.node_tree.nodes.new('ShaderNodeSeparateColor')
            mat.node_tree.links.new(arm.outputs['Color'], separate.inputs['Color'])
            mat.node_tree.links.new(separate.outputs['Green'], shader.inputs['Roughness'])
            mat.node_tree.links.new(separate.outputs['Blue'], shader.inputs['Metallic'])
        custom = image_node('Cstm')
        if custom and diffuse:
            custom.image.colorspace_settings.name = 'Non-Color'
            split = mat.node_tree.nodes.new('ShaderNodeSeparateColor')
            mat.node_tree.links.new(custom.outputs['Color'], split.inputs['Color'])
            color_output = diffuse.outputs['Color']
            for letter, channel in (('R','Red'),('G','Green'),('B','Blue')):
                color = props.get('Colors',{}).get('CustomMask'+letter+'_Color')
                if not color or not props.get('Scalars',{}).get('CustomMask'+letter+'_Use',0): continue
                tint = mat.node_tree.nodes.new('ShaderNodeMixRGB')
                tint.blend_type = 'MULTIPLY'
                tint.inputs[2].default_value = (color['R'],color['G'],color['B'],1)
                mat.node_tree.links.new(split.outputs[channel], tint.inputs[0])
                mat.node_tree.links.new(color_output, tint.inputs[1])
                color_output = tint.outputs[0]
            mat.node_tree.links.new(color_output, shader.inputs['Base Color'])
        if diffuse and 'SpiritKnt' in mat.name:
            cutoff = mat.node_tree.nodes.new('ShaderNodeMath')
            cutoff.operation = 'GREATER_THAN'; cutoff.inputs[1].default_value = 0.33333
            mat.node_tree.links.new(diffuse.outputs['Alpha'], cutoff.inputs[0])
            mat.node_tree.links.new(cutoff.outputs[0], shader.inputs['Alpha'])
    if 'Hair' in mat.name:
        for link in list(shader.inputs['Base Color'].links): mat.node_tree.links.remove(link)
        shader.inputs['Base Color'].default_value = (0.13, 0.055, 0.023, 1)
        hair = image_node('DIRO')
        if hair: mat.node_tree.links.new(hair.outputs['Alpha'], shader.inputs['Alpha'])
        shader.inputs['Roughness'].default_value = 0.32
    if 'Eye' in mat.name:
        shader.inputs['Base Color'].default_value = (0.15, 0.23, 0.26, 1)
        shader.inputs['Roughness'].default_value = 0.2
    mat_report.append({'material':mat.name, 'base_texture': diffuse.image.name if diffuse else None})

anim = next(source.rglob('GF_SW_LobbyIdle.ueanim'))
matched = []
for name, rig in rigs.items():
    if rig.type != 'ARMATURE': continue
    action, dto = UEFormatImport(UEAnimOptions(override_skeleton=rig, import_curves=False)).import_file_with_dto(anim)
    bone_names = {b.name.lower() for b in rig.data.bones}
    matched.append({'part':name,'bones':len(bone_names),'matching_tracks':sum(t.name.lower() in bone_names for t in dto.tracks)})
scene.frame_set(1)
bpy.context.view_layer.update()
body = rigs['GF_SpiritKnt_01_Body']
hands = [b.name for b in body.pose.bones if any(t in b.name.lower() for t in ('hand','weapon','wp_','wep'))]
print('HAND BONES', hands, flush=True)
right = next((b for b in body.pose.bones if b.name.lower() in ('hand_r','bip001-r-hand','bip001 r hand')), None)
if right is None:
    right = next(b for b in body.pose.bones if 'hand' in b.name.lower() and ('r_' in b.name.lower() or '_r' in b.name.lower()))
weapon.matrix_world = body.matrix_world @ right.matrix
weapon.scale *= 0.85
print('WEAPON', weapon.name, 'hand', right.name, 'position', list(weapon.location), flush=True)

def aim(obj, point): obj.rotation_euler = (Vector(point)-obj.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.mesh.primitive_plane_add(size=200, location=(0,0,-0.02))
floor = bpy.context.object
floor.name = 'Studio floor'
mat = bpy.data.materials.new('Floor')
mat.diffuse_color = (0.025,0.04,0.065,1)
floor.data.materials.append(mat)
for pos,power,size,color in [((3,-4,5),700,4,(0.8,0.9,1)),((-3,-2,3),500,3,(1,0.78,0.55)),((0,3,4),900,3,(0.45,0.65,1))]:
    bpy.ops.object.light_add(type='AREA', location=pos)
    light = bpy.context.object
    light.data.energy = power; light.data.shape = 'DISK'; light.data.size = size; light.data.color = color
    aim(light,(0,0,1))
bpy.ops.object.camera_add(location=(3,-6,2.3))
scene.camera = bpy.context.object
aim(scene.camera,(0,0,1))
scene.camera.data.type = 'ORTHO'; scene.camera.data.ortho_scale = 2.7
scene.render.image_settings.file_format = 'PNG'
scene.render.filepath = str(out/'aion2-female-knight.png')
(out/'assembly-report.json').write_text(json.dumps({'parts':names,'animation_pose':anim.name,'matched':matched,'materials':mat_report,'weapon_hand':right.name},indent=2))
bpy.ops.wm.save_as_mainfile(filepath=str(out/'aion2-female-knight.blend'))
bpy.ops.render.render(write_still=True)
