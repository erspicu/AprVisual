#!/bin/bash
# Pi 5 freq LOCK for clean cycle measurement: record current governor -> set performance (pins scaling_max).
# Restore with pi_freq_restore.sh (a reboot also restores the RPi default 'ondemand').
cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor > ~/.cpufreq_baseline
sudo cpupower frequency-set -g performance >/dev/null
echo "LOCKED: governor=performance (pinned $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_max_freq) kHz). baseline: $(cat ~/.cpufreq_baseline)"
awk '{print "  cur freq: "$1/1000" MHz"}' /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq
