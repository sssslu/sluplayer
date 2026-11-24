# SluPlayer frame generation, run by mpv's vapoursynth filter (see PlayerEngine "Frame generation").
# RIFE generates (factor - 1) frames between every two source frames, on the GPU (Vulkan).
# user_data: "<rife plugin dll>|<model folder>|<factor>|<gpu threads>|<max width>|<max height>|<error file>"
#
# - Source frames pass through untouched; only the frames in between are generated.
# - mpv rebuilds this script on every seek and RIFE needs about a second to start, so RIFE is loaded in the
#   background: playback starts at once with repeated frames and switches to generated ones when RIFE is ready.
# - Nothing is generated across a cut (the two frames differ too much): the earlier frame is repeated there.
import threading
import time
import vapoursynth as vs

core = vs.core
plugin, model, factor, threads, max_w, max_h, error_file = user_data.split("|")
factor, threads, max_w, max_h = int(factor), int(threads), int(max_w), int(max_h)

src = video_in
if src.width > max_w or src.height > max_h:
    # Larger than the screen: generating at screen size costs far less and shows the same picture.
    scale = min(max_w / src.width, max_h / src.height)
    step_w, step_h = 2 << src.format.subsampling_w, 2 << src.format.subsampling_h
    src = core.resize.Bicubic(src, width=max(64, int(src.width * scale) // step_w * step_w),
                              height=max(64, int(src.height * scale) // step_h * step_h),
                              filter_param_a=0, filter_param_b=0.5)

# How much two neighbouring frames differ, measured on small grey copies: above CUT it is a cut.
CUT = 0.12
small = core.resize.Bilinear(src, width=160, height=96, format=vs.GRAY8)
diff = core.std.PlaneStats(small, core.std.Trim(small, first=1))

ready = {}

def build():
    """RIFE and the conversions around it. YUV <-> RGB uses the same fixed assumptions in both directions, so the
    generated frames match the source frames whatever the video's real matrix, range and chroma position are
    (mpv gives the frames it gets back the source's colour description)."""
    if not hasattr(core, "rife"):
        core.std.LoadPlugin(plugin)
    rgb = core.std.RemoveFrameProps(src, ["_Range", "_ColorRange", "_ChromaLocation", "_Matrix", "_Transfer", "_Primaries"])
    rgb = core.resize.Bicubic(rgb, format=vs.RGBS, matrix_in_s="709", range_in_s="full", chromaloc_in_s="left")
    out = core.rife.RIFE(rgb, model_path=model, factor_num=factor, factor_den=1, gpu_thread=threads)
    made = {}
    for k in range(1, factor):
        c = core.std.SelectEvery(out, cycle=factor, offsets=k)
        c = core.resize.Bicubic(c, format=src.format.id, matrix_s="709", range_s="full", chromaloc_s="left",
                                dither_type="ordered")
        made[k] = core.std.CopyFrameProps(c, src)
    ready.update(made)

def build_later(env):
    time.sleep(0.35)  # a seek that is followed by another one never gets here
    try:
        with env.use():
            try:
                build()
            except Exception as e:
                if env.alive:  # not just dropped meanwhile (seek, file change): RIFE cannot run on this PC
                    with open(error_file, "w", encoding="utf-8") as f:
                        f.write(f"{type(e).__name__}: {e}")
    except Exception:
        pass

threading.Thread(target=build_later, args=(vs.get_current_environment(),), daemon=True).start()

def between(k):
    def pick(n, f):
        made = ready.get(k)
        return src if made is None or f.props["PlaneStatsDiff"] > CUT else made
    return core.std.FrameEval(src, pick, prop_src=diff)

core.std.Interleave([src] + [between(k) for k in range(1, factor)]).set_output()
