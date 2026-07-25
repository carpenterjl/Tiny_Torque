# Datasheet sources

Files in this folder, with where each came from and when.

| File | Size | Retrieved from | Date |
|---|---:|---|---|
| `BST-BNO055-DS000.pdf` | 2 052 876 B | <https://www.bosch-sensortec.com/media/boschsensortec/downloads/datasheets/bst-bno055-ds000.pdf> — Bosch Sensortec, the manufacturer | 2026-07-24 |
| `VL53L1X-datasheet.pdf` | 903 993 B | <https://www.pololu.com/file/0J1507/vl53l1x.pdf> — Pololu's mirror of the STMicroelectronics datasheet | 2026-07-24 |

The VL53L1X datasheet is mirrored rather than taken from
<https://www.st.com/resource/en/datasheet/vl53l1x.pdf> because ST's own CDN timed out
repeatedly. The content is ST's document; the mirror is a distributor copy.

## Parts with no datasheet PDF

These are documented from manufacturer product pages instead, quoted in
[`../bill_of_materials.md`](../bill_of_materials.md):

| Part | Page |
|---|---|
| Castle Creations 1410-3800Kv motor | <https://www.castlecreations.com/en/1410-3800kv-sensored-motor-5mm-060-0066-00> |
| Savox SC-1251MG servo | <https://www.savoxusa.com/products/savsc1251mg-low-profile-digital-servo> |

Neither manufacturer publishes an engineering datasheet for these parts — Castle's page
carries mechanical dimensions and Kv but no winding resistance, no-load current or rotor
inertia, and Savox publishes a speed/torque pair per voltage and nothing else. That gap
is exactly what [`../derived_parameters.md`](../derived_parameters.md) §3 exists to
document.
