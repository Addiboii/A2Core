"""Blender-only placement proof for one recovered starter-world cell."""
import json
import math
import sys
from pathlib import Path
import bpy
from mathutils import Vector, Quaternion

root=Path(__file__).resolve().parents[1]/'research/local'
sys.path.insert(0,str(root/'tooling/blender-python-deps'))
sys.path.insert(0,str(root/'tooling/h4lfheart-UEFormat-d858fe3/plugins/blender'))
from io_scene_ueformat.importer.import_context import UEFormatImport
from io_scene_ueformat.options import UEModelOptions

source=root/'exports/world-region'
output=root/'world/cell-preview';output.mkdir(exist_ok=True)
rows=[r for r in json.loads((root/'world/light-starter-manifest.json').read_text())['placements'] if r['source_file'].startswith('package-001-')]
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
cache={};objects=[]
def vector(d,default):return Vector(tuple(d.get(k,default) for k in ('X','Y','Z')))
origin=sum((vector(r['transform']['RelativeLocation'],0) for r in rows),Vector())/len(rows)
for row in rows:
    t=row['transform']
    if 'AttachParent' in t:raise ValueError('Parented transforms require composition')
    name=row['mesh']['ObjectName'].split("'")[1]
    if name not in cache:
        cache[name]=UEFormatImport(UEModelOptions(import_collision=False)).import_file(next(source.rglob(name+'.uemodel')))
        obj=cache[name]
    else:
        obj=cache[name].copy();bpy.context.collection.objects.link(obj)
    p=(vector(t['RelativeLocation'],0)-origin)*0.01;p.y=-p.y;obj.location=p
    rot=t.get('RelativeRotation',{});pitch,yaw,roll=[math.radians(rot.get(k,0))/2 for k in ('Pitch','Yaw','Roll')]
    sp,cp=math.sin(pitch),math.cos(pitch);sy,cy=math.sin(yaw),math.cos(yaw);sr,cr=math.sin(roll),math.cos(roll)
    x=cr*sp*sy-sr*cp*cy;y=-cr*sp*cy-sr*cp*sy;z=cr*cp*sy-sr*sp*cy;w=cr*cp*cy+sr*sp*sy
    obj.rotation_mode='QUATERNION';obj.rotation_quaternion=Quaternion((w,-x,y,-z))
    obj.scale=vector(t.get('RelativeScale3D',{}),1);objects.append(obj)
textures={p.stem:p for p in source.rglob('*.png')}
materials={p.stem:json.loads(p.read_text()) for p in source.rglob('*.json')}
for m in bpy.data.materials:
    m.use_nodes=True;shader=m.node_tree.nodes.get('Principled BSDF');shader.inputs['Roughness'].default_value=.75
    tex=materials.get(m.name,{}).get('Textures',{});ref=tex.get('BaseColor') or tex.get('PM_Diffuse')
    name=ref['ObjectName'].split("'")[1] if ref else None
    if name in textures:
        n=m.node_tree.nodes.new('ShaderNodeTexImage');n.image=bpy.data.images.load(str(textures[name]),check_existing=True)
        m.node_tree.links.new(n.outputs['Color'],shader.inputs['Base Color'])
bpy.context.view_layer.update()
points=[o.matrix_world@Vector(c) for o in objects for c in o.bound_box]
lo=Vector(tuple(min(p[i] for p in points) for i in range(3)));hi=Vector(tuple(max(p[i] for p in points) for i in range(3)))
center=(lo+hi)/2;extent=max(hi-lo)
def aim(o):o.rotation_euler=(center-o.location).to_track_quat('-Z','Y').to_euler()
scene=bpy.context.scene;scene.render.engine='CYCLES';scene.cycles.samples=24;scene.cycles.use_denoising=True
scene.render.resolution_x=1100;scene.render.resolution_y=900;scene.render.resolution_percentage=100
scene.world.color=(.22,.25,.3)
bpy.ops.object.light_add(type='SUN',rotation=(.5,-.4,-.6));bpy.context.object.data.energy=3
bpy.ops.object.camera_add(location=center+Vector((1,-1.4,.8))*extent);scene.camera=bpy.context.object;aim(scene.camera)
scene.camera.data.type='ORTHO';scene.camera.data.ortho_scale=extent*1.3;scene.camera.data.clip_end=10000
scene.render.image_settings.file_format='PNG';scene.render.filepath=str(output/'light-starter-cell.png')
(output/'placement-proof.json').write_text(json.dumps({'placed_objects':len(objects),'unique_meshes':len(cache),'recenter_origin_unreal':list(origin),'limitation':'One streamed cell only; omitted transforms use identity defaults for this visual test. No neighboring cells, terrain or gameplay reconstructed.'},indent=2))
bpy.ops.wm.save_as_mainfile(filepath=str(output/'light-starter-cell.blend'));bpy.ops.render.render(write_still=True)
