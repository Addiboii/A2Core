"""Blender-only preview of recovered scenery and one landscape tile.

Requires local UEFormat exports, Blender dependencies and placement manifests.
Materials and lighting approximate the game; this is not a gameplay client.
"""
import json
import math
import sys
from pathlib import Path
import bpy
from mathutils import Vector, Quaternion

root = Path(__file__).resolve().parents[1] / 'research/local'
sys.path.insert(0, str(root/'tooling/blender-python-deps'))
sys.path.insert(0, str(root/'tooling/h4lfheart-UEFormat-d858fe3/plugins/blender'))
from io_scene_ueformat.importer.import_context import UEFormatImport
from io_scene_ueformat.options import UEModelOptions

source = root/'exports/sven-region'
output = root/'world/region-preview'
output.mkdir(exist_ok=True)
rows = json.loads((root/'world/sven-visual-placements.json').read_text())
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
origin = Vector((8831.28, -64720.797, 15901.368))
cache = {}
objects = []
files = {p.stem:p for p in source.rglob('*.uemodel')}

def vector(d, default):
    return Vector(tuple(d.get(k, default) for k in ('X','Y','Z')))

def place(obj, t):
    if 'AttachParent' in t:
        raise ValueError('Parented transforms require composition')
    p = (vector(t['RelativeLocation'], 0)-origin)*0.01
    p.y = -p.y
    obj.location = p
    rot = t.get('RelativeRotation', {})
    pitch,yaw,roll = [math.radians(rot.get(k,0))/2 for k in ('Pitch','Yaw','Roll')]
    sp,cp = math.sin(pitch),math.cos(pitch)
    sy,cy = math.sin(yaw),math.cos(yaw)
    sr,cr = math.sin(roll),math.cos(roll)
    x=cr*sp*sy-sr*cp*cy; y=-cr*sp*cy-sr*cp*sy
    z=cr*cp*sy-sr*sp*cy; w=cr*cp*cy+sr*sp*sy
    obj.rotation_mode='QUATERNION'
    obj.rotation_quaternion=Quaternion((w,-x,y,-z))
    obj.scale=vector(t.get('RelativeScale3D',{}),1)

for row in rows:
    name = row['mesh']['ObjectName'].split("'")[1]
    if name not in cache:
        cache[name] = UEFormatImport(UEModelOptions(import_collision=False)).import_file(files[name])
        obj = cache[name]
    else:
        obj = cache[name].copy()
        bpy.context.collection.objects.link(obj)
    place(obj, row['transform'])
    objects.append(obj)

terrain_name='LandscapeStreamingProxy_E3ONUUU4E8QF57HMZRJ6VY22N_254_4_1_0'
terrain=UEFormatImport(UEModelOptions(import_collision=False)).import_file(files[terrain_name])
root_component=json.loads((root/'world/sven-region-a/package-014-export-00023.json').read_text())
place(terrain, root_component['Properties'])
objects.append(terrain)
textures = {p.stem:p for p in source.rglob('*.png')}
materials = {p.stem:json.loads(p.read_text()) for p in source.rglob('*.json')}
for material in bpy.data.materials:
    material.use_nodes=True
    shader=material.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Roughness'].default_value=.8
    tex=materials.get(material.name,{}).get('Textures',{})
    candidates=[tex.get(k) for k in ('T_BaseColor','BaseColor','PM_Diffuse')]
    names=[r['ObjectName'].split("'")[1] for r in candidates if r]
    name=next((n for n in names if n in textures and 'UVchecker' not in n),None)
    if name in textures:
        node=material.node_tree.nodes.new('ShaderNodeTexImage')
        node.image=bpy.data.images.load(str(textures[name]),check_existing=True)
        material.node_tree.links.new(node.outputs['Color'],shader.inputs['Base Color'])

# Original landscape height geometry; a simple earth shader until layer shaders
# and runtime virtual textures can be reconstructed.
earth=bpy.data.materials.new('Preview terrain earth')
earth.diffuse_color=(.22,.27,.12,1)
earth.use_nodes=True
earth.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value=earth.diffuse_color
earth.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value=.95
terrain.data.materials.clear()
terrain.data.materials.append(earth)
for face in terrain.data.polygons:
    face.material_index=0
    face.use_smooth=True
bpy.context.view_layer.update()
scene=bpy.context.scene
scene.render.engine='CYCLES'
scene.cycles.samples=24
scene.cycles.use_denoising=True
scene.render.resolution_x=1400
scene.render.resolution_y=1000
scene.render.resolution_percentage=100
scene.world.color=(.28,.32,.4)
scene.view_settings.exposure=.7
bpy.ops.object.light_add(type='SUN', rotation=(.5,-.4,-.6))
bpy.context.object.data.energy=3
center=Vector((0,0,10))
bpy.ops.object.camera_add(location=center+Vector((145,-180,170)))
scene.camera=bpy.context.object
scene.camera.rotation_euler=(center-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.camera.data.type='ORTHO'
scene.camera.data.ortho_scale=220
scene.camera.data.clip_end=10000
scene.render.image_settings.file_format='PNG'
scene.render.filepath=str(output/'light-starter-region.png')
proof={'placed_scenery':len(rows),'unique_scenery_meshes':len(cache),'landscape_tiles':1,
       'recenter_origin_unreal':list(origin),
       'terrain_bounds_blender':[list(terrain.matrix_world@Vector(c)) for c in terrain.bound_box],
       'limitations':['Omitted transforms use identity defaults for this preview.',
                      'Simplified materials and lighting; terrain uses a preview earth shader.',
                      'Foliage instances, gameplay actors, effects and neighboring terrain omitted.',
                      'One referenced streaming package was absent from mounted files.']}
(output/'placement-proof.json').write_text(json.dumps(proof,indent=2))
bpy.ops.wm.save_as_mainfile(filepath=str(output/'light-starter-region.blend'))
bpy.ops.render.render(write_still=True)
