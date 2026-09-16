"""Create/export the isolated GP-04 library; never replace an existing scene or object."""
import bpy
import json
import math
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parent
scene = bpy.data.scenes.get('AC_GraphScreen_Preview')
if scene is None:
    scene = bpy.data.scenes.new('AC_GraphScreen_Preview')
model = bpy.data.collections.get('AC_GraphScreen')
if model is None:
    model = bpy.data.collections.new('AC_GraphScreen')
    scene.collection.children.link(model)
studio = bpy.data.collections.get('AC_GraphScreen_Studio')
if studio is None:
    studio = bpy.data.collections.new('AC_GraphScreen_Studio')
    scene.collection.children.link(studio)

def material(name, rgb, metal=0, rough=.45):
    m = bpy.data.materials.get('GP04_' + name)
    if m is None:
        m = bpy.data.materials.new('GP04_' + name)
        m.diffuse_color = (*rgb, 1)
        m.use_nodes = True
        p = m.node_tree.nodes.get('Principled BSDF')
        p.inputs['Base Color'].default_value = (*rgb, 1)
        p.inputs['Metallic'].default_value = metal
        p.inputs['Roughness'].default_value = rough
    return m

body = material('Housing', (.24,.27,.29), .65)
edge = material('Edge', (.075,.09,.1), .4)
bezel = material('Bezel', (.018,.025,.028), .3)
glass = material('Glass', (.009,.018,.019), .15, .28)
steel = material('Steel', (.45,.49,.5), .8)
amber = material('Amber', (.85,.36,.045), .25)
letter = material('Legend', (.6,.69,.68), .1)

def mesh_object(name, vertices, faces, mat):
    obj = bpy.data.objects.get('GP04_' + name)
    if obj is not None:
        return obj
    mesh = bpy.data.meshes.new('GP04_' + name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new('GP04_' + name, mesh)
    model.objects.link(obj)
    mesh.materials.append(mat)
    return obj

def box(name, center, size, mat, bevel=0):
    x,y,z = size
    verts = [(a*x/2,b*y/2,c*z/2) for a,b,c in
             [(-1,-1,-1),(-1,-1,1),(-1,1,-1),(-1,1,1),(1,-1,-1),(1,-1,1),(1,1,-1),(1,1,1)]]
    obj = mesh_object(name, verts, [(0,4,6,2),(1,3,7,5),(0,1,5,4),(2,6,7,3),(0,2,3,1),(4,5,7,6)], mat)
    obj.location = center
    if bevel and not obj.modifiers:
        m = obj.modifiers.new('Machined edges','BEVEL'); m.width=bevel; m.segments=2
        m = obj.modifiers.new('Weighted normals','WEIGHTED_NORMAL')
    return obj

box('Chassis', (0,0,-.025), (.94,.96,.20), body, .018)
box('BottomSeam', (0,0,-.09), (.96,.98,.05), edge, .012)
box('TopRim', (0,0,.076), (.94,.96,.074), body, .016)
box('ScreenBezel', (0,0,.109), (.876,.80,.026), bezel, .016)
box('Glass', (0,0,.120), (.814,.71,.006), glass, .009)
for x in (-.44,.44):
    box('SideRail'+str(x), (x,0,.0), (.025,.88,.20), steel, .003)
for y in (-.454,.454):
    box('Bumper'+str(y), (0,y,-.018), (.86,.055,.19), edge, .008)
for y in (-.427,.427):
    for x in (-.403,.403):
        name = 'Bolt'+str((x,y))
        verts = [(x+.014*math.cos(a*math.tau/8), y+.014*math.sin(a*math.tau/8), z)
                 for z in (.109,.118) for a in range(8)]
        faces = [(i,(i+1)%8,(i+1)%8+8,i+8) for i in range(8)] + [tuple(range(7,-1,-1)),tuple(range(8,16))]
        mesh_object(name,verts,faces,steel)
        box(name+'Slot',(x,y,.1185),(.019,.003,.001),bezel)
for i in range(8):
    box('Vent'+str(i),(.465,-.27+i*.075,-.023),(.006,.045,.04),bezel,.003)

# Four cable mouths: native anchors sit 1/16 inward from these rings.
ports=[]
for i in range(4):
    y = -.1875+i*.125
    z = -.0625
    ports.append([-.5,z,-y])
    verts=[]
    for x,r in [(-.47,.035011295),(-.5,.035011295),(-.5,.022),(-.47,.022)]:
        verts += [(x,y+r*math.cos(a*math.tau/24),z+r*math.sin(a*math.tau/24)) for a in range(24)]
    faces=[]
    for ring in range(4):
        for a in range(24):
            faces.append((ring*24+a,ring*24+(a+1)%24,((ring+1)%4)*24+(a+1)%24,((ring+1)%4)*24+a))
    mesh_object('Input'+str(i+1),verts,faces,amber)
    box('SocketBack'+str(i),(-.469,y,z),(.012,.067,.067),bezel,.004)

def text(name, value, location, size):
    obj = bpy.data.objects.get('GP04_'+name)
    if obj is None:
        data=bpy.data.curves.new('GP04_'+name,'FONT'); data.body=value; data.size=size
        data.align_x='CENTER'; data.extrude=.0002
        obj=bpy.data.objects.new('GP04_'+name,data); model.objects.link(obj); data.materials.append(letter)
    obj.location=location
text('Name','GP-04  /  GRAPH SCREEN',(0,.413,.114),.026)
text('Footer','INPUT 1-4     |     DATA / PLOT',(0,-.442,.114),.014)

# Preview graphics are studio-only; runtime owns the live screen pixels/lines.
trace_mat = material('PreviewSignal',(.1,.85,.67),0)
if bpy.data.objects.get('GP04_PreviewTrace') is None:
    curve=bpy.data.curves.new('GP04_PreviewTrace','CURVE'); curve.dimensions='3D'; curve.bevel_depth=.0015; curve.bevel_resolution=1
    sp=curve.splines.new('POLY'); sp.points.add(79)
    for i,p in enumerate(sp.points):
        x=-.33+i*.66/79; p.co=(x,.10*math.sin(i*.16)+.08*math.sin(i*.037),.127,1)
    ob=bpy.data.objects.new('GP04_PreviewTrace',curve); studio.objects.link(ob); curve.materials.append(trace_mat)
for axis in range(2):
    for i in range(7):
        name='GP04_PreviewGrid'+str(axis)+'_'+str(i)
        if bpy.data.objects.get(name) is None:
            curve=bpy.data.curves.new(name,'CURVE'); curve.dimensions='3D'; curve.bevel_depth=.00035
            sp=curve.splines.new('POLY'); sp.points.add(1)
            a=-.3+i*.1
            sp.points[0].co=((- .34,a,.126,1) if axis==0 else (a,-.3,.126,1))
            sp.points[1].co=((.34,a,.126,1) if axis==0 else (a,.3,.126,1))
            ob=bpy.data.objects.new(name,curve); studio.objects.link(ob); curve.materials.append(edge)

camera=bpy.data.objects.get('GP04_Camera')
if camera is None:
    camera=bpy.data.objects.new('GP04_Camera',bpy.data.cameras.new('GP04_Camera')); studio.objects.link(camera)
camera.location=(-1.5,-1.9,2.25)
camera.rotation_euler=(Vector((0,0,0))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO'; camera.data.ortho_scale=1.63; scene.camera=camera
for name,pos,power,size in [('Key',(-2,-1,4),400,3),('Fill',(2,1,2),250,2),('Rim',(-1,3,2),300,2)]:
    ob=bpy.data.objects.get('GP04_'+name)
    if ob is None:
        data=bpy.data.lights.new('GP04_'+name,'AREA'); ob=bpy.data.objects.new('GP04_'+name,data); studio.objects.link(ob)
    ob.location=pos; ob.data.energy=power; ob.data.shape='DISK'; ob.data.size=size
    ob.rotation_euler=(-ob.location).to_track_quat('-Z','Y').to_euler()
if scene.world is None: scene.world=bpy.data.worlds.new('GP04_World')
scene.world.color=(.08,.08,.08)
scene.render.engine='CYCLES'; scene.cycles.samples=32
scene.render.resolution_x=1100; scene.render.resolution_y=900; scene.render.resolution_percentage=100
scene.render.film_transparent=True
scene.render.image_settings.file_format='PNG'
scene.render.filepath='//graph-screen-preview.png'

original = bpy.context.window.scene if bpy.context.window else None
if bpy.context.window: bpy.context.window.scene=scene
bpy.context.view_layer.update()
deps=bpy.context.evaluated_depsgraph_get()
parts={}
mins=[float('inf')]*3; maxs=[-float('inf')]*3
for ob in model.objects:
    ev=ob.evaluated_get(deps); mesh=ev.to_mesh(); mesh.calc_loop_triangles()
    color=list(ob.data.materials[0].diffuse_color)
    key=ob.data.materials[0].name
    part=parts.setdefault(key,dict(name=key,color=color,vertices=[],normals=[],triangles=[]))
    for tri in mesh.loop_triangles:
        for li in tri.loops:
            v=ev.matrix_world @ mesh.vertices[mesh.loops[li].vertex_index].co
            n=ev.matrix_world.to_3x3() @ mesh.corner_normals[li].vector
            n.normalize(); p=[v.x,v.z,-v.y]
            for a in range(3): mins[a]=min(mins[a],p[a]); maxs[a]=max(maxs[a],p[a])
            part['triangles'].append(len(part['vertices'])); part['vertices'].append(p); part['normals'].append([n.x,n.z,-n.y])
    ev.to_mesh_clear()
assert len(ports)==4 and maxs[1] < .131 and mins[1] >= -.126
document=dict(parts=list(parts.values()),ports=ports,bounds=[1,.25,1],surface=[.8,.7,.13])
(ROOT/'graph-screen.mesh.json').write_text(json.dumps(document,separators=(',',':')),encoding='utf-8')
bpy.data.libraries.write(str(ROOT/'graph-screen.blend'),{scene},fake_user=True)
for area in bpy.context.screen.areas if bpy.context.screen else []:
    if area.type=='VIEW_3D':
        area.spaces.active.region_3d.view_perspective='CAMERA'
bpy.ops.render.render(write_still=False,scene=scene.name)
bpy.data.images['Render Result'].save_render(str(ROOT/'graph-screen-preview.png'),scene=scene)
result={'scene':scene.name,'objects':len(model.objects),'bounds':[mins,maxs],'parts':len(parts),'triangles':sum(len(p['triangles'])//3 for p in parts.values()),'user_scene_preserved':original.name if original else None}
