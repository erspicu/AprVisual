#!/bin/bash
GOV=$(cat ~/.cpufreq_baseline 2>/dev/null || echo ondemand)
sudo cpupower frequency-set -g "$GOV" >/dev/null
echo "RESTORED: governor=$GOV (cur $(($(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq)/1000)) MHz)"
