"""Candidate equipped starter inventory using observed 0x5611 record widths.

The official sample has 10 items, including eight equipped records of 92 bytes.
No captured item UID/account state is reused. Unresolved optional fields are
neutral; this intentionally does not claim a general inventory serializer.
"""
import struct
try:
    from . import class_skills
except ImportError:
    import class_skills


def build_equipped_inventory(item_id, item_level=1, item_key=1, equip_slot=1):
    # Observed reply prefix (inventory metadata), one item, equipped marker.
    out = bytearray(b"\x11\x56\x10\x02\x01")
    out += b"\x01" + struct.pack("<qiqBi", item_key, item_id, 1, 11, equip_slot)
    # Lock/maker/expiry and neutral enchant fields; optional layout unresolved.
    out += bytes(23)
    out += struct.pack("<iBB", item_level, 1, equip_slot)
    out += bytes(16)
    out += b"\x03" + bytes(20)  # observed empty customization/trailing fields
    out += bytes(9)             # observed depot trailer
    return bytes(out)


def starter_weapon(profile):
    # Item.dat equip_wp_<Type>_[LD]_c_begin_001a_b, level 1, for the class weapon type.
    return class_skills.starter_weapon(profile["class"], profile["race"]) if profile else None
