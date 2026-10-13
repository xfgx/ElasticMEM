#!/bin/bash
# Синтез под Lattice ECP5 (открытый тулчейн Yosys + nextpnr) — площадь и частота аппаратного блока.
mkdir -p reports
for m in extract compressor decompressor regwrap; do
  yowasp-yosys -q -p "read_verilog verilog/bdi_$m.v; synth_ecp5 -top toplevel -json reports/bdi_$m.json; tee -q -o reports/bdi_$m.stat.txt stat"
  echo "== $m"; grep -E "LUT4|CCU2C|TRELLIS_FF|PFUMX|L6MUX21|Number of cells" reports/bdi_$m.stat.txt | sed 's/^ */  /'
done
# размещение и трассировка обёртки с регистрами: оценка Fmax
yowasp-nextpnr-ecp5 --85k --package CABGA381 --out-of-context --json reports/bdi_regwrap.json --freq 100 --timing-allow-fail \
  --report reports/bdi_regwrap.timing.json 2> reports/nextpnr.log
grep -E "Max frequency|TRELLIS_COMB|TRELLIS_FF" reports/nextpnr.log | tail -8
