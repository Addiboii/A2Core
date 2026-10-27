"""Run inside Blender: import local UEFormat assets and render a walk preview.

Paths are project-relative; downloaded UEFormat and Blender dependencies stay
under research/local/tooling. Materials approximate exported base colors only.
"""
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'
sys.path.insert(0, str(LOCAL / 'tooling/blender-python-deps'))
sys.path.insert(0, str(LOCAL / 'tooling/h4lfheart-UEFormat-d858fe3/plugins/blender'))
from io_scene_ueformat.importer.import_context import UEFormatImport
from io_scene_ueformat.options import UEModelOptions, UEAnimOptions

source = LOCAL / 'exports/character-video'
output = LOCAL / 'preview'
output.mkdir(exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.samples = 12
scene.cycles.use_denoising = True
scene.render.resolution_x = 640
scene.render.resolution_y = 720
scene.render.resolution_percentage = 100
scene.render.fps = 24
scene.world.color = (0.16, 0.16, 0.16)
scene.view_settings.view_transform = 'AgX'

rigs = []
for name in ('GM_BaseFull', 'GM_BaseHead'):
    path = next(source.rglob(name + '.uemodel'))
    rig = UEFormatImport(UEModelOptions(import_sockets=False)).import_file(path)
    rigs.append(rig)

materials = {p.stem: p for p in source.rglob('*.json')}
textures = {p.stem: p for p in source.rglob('*.png')}
material_report = []
for mat in bpy.data.materials:
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Roughness'].default_value = 0.6
    shader.inputs['Base Color'].default_value = (0.55, 0.36, 0.25, 1)
    info = json.loads(materials[mat.name].read_text()) if mat.name in materials else {}
    tex = info.get('Textures', {}).get('BaseColor') or info.get('Textures', {}).get('PM_Diffuse')
    name = tex['ObjectName'].split("'")[1] if tex else None
    # PM_Diffuse is only a heuristic: the head's black hair mask is not albedo.
    if name and ('Mask' in name or name == 'T_Base_M'):
        name = None
    if name in textures:
        node = mat.node_tree.nodes.new('ShaderNodeTexImage')
        node.image = bpy.data.images.load(str(textures[name]), check_existing=True)
        mat.node_tree.links.new(node.outputs['Color'], shader.inputs['Base Color'])
    material_report.append({'material': mat.name, 'base_color_texture': name, 'resolved': name in textures})

animation_path = next(source.rglob('GM_BW_Walk_F.ueanim'))
reports = []
for rig in rigs:
    action, dto = UEFormatImport(UEAnimOptions(override_skeleton=rig, import_curves=False)).import_file_with_dto(animation_path)
    names = {b.name.lower() for b in rig.data.bones}
    matched = [t.name for t in dto.tracks if t.name.lower() in names]
    fps = dto.metadata.frames_per_second
    frame_count = dto.metadata.num_frames
    # Preserve source timing while repeating the walk for six seconds.
    for fc in action.fcurves:
        for key in fc.keyframe_points:
            key.co.x = 1 + key.co.x * scene.render.fps / fps
        fc.modifiers.new('CYCLES')
    reports.append({'rig': rig.name, 'bones': len(names), 'tracks': len(dto.tracks),
                    'matched_tracks': len(matched), 'source_frames': frame_count,
                    'source_fps': fps, 'action': action.name})

def aim(obj, point):
    obj.rotation_euler = (Vector(point) - obj.location).to_track_quat('-Z', 'Y').to_euler()

bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -0.015))
floor = bpy.context.object
floor.name = 'Studio floor'
mat = bpy.data.materials.new('Floor')
mat.diffuse_color = (0.07, 0.085, 0.11, 1)
floor.data.materials.append(mat)
for location, energy, size in [((3, -4, 5), 550, 4), ((-3, -1, 3), 350, 3), ((0, 3, 4), 650, 3)]:
    bpy.ops.object.light_add(type='AREA', location=location)
    light = bpy.context.object
    light.data.energy = energy
    light.data.shape = 'DISK'
    light.data.size = size
    aim(light, (0, 0, 1))
bpy.ops.object.camera_add(location=(3.2, -5.0, 2.2))
scene.camera = bpy.context.object
aim(scene.camera, (0, 0, 0.95))
scene.camera.data.type = 'ORTHO'
scene.camera.data.ortho_scale = 2.35
scene.frame_start = 1
scene.frame_end = 144
scene.frame_set(1)

# Record evaluated mesh movement rather than merely the presence of keyframes.
motion = []
for obj in [o for o in bpy.data.objects if o.type == 'MESH' and o != floor]:
    samples = []
    for frame in (1, 9, 17):
        scene.frame_set(frame)
        evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        mesh = evaluated.to_mesh()
        samples.append([list(mesh.vertices[i].co) for i in range(0, len(mesh.vertices), max(1, len(mesh.vertices)//100))])
        evaluated.to_mesh_clear()
    delta = max((Vector(a)-Vector(b)).length for a,b in zip(samples[0], samples[1]))
    motion.append({'mesh': obj.name, 'sampled_motion_meters': delta})
scene.frame_set(1)
scene.render.image_settings.file_format = 'PNG'
scene.render.filepath = str(output / 'frames/frame-')
(output / 'import-report.json').write_text(json.dumps({'animations': reports, 'materials': material_report, 'motion': motion}, indent=2))
bpy.ops.wm.save_as_mainfile(filepath=str(output / 'aion2-walk-preview.blend'))
scene.render.image_settings.file_format = 'PNG'
scene.render.filepath = str(output / 'frame-0001.png')
bpy.ops.render.render(write_still=True)
