/*
 * Test-only public libavformat/libavcodec probe for Mani's fixed FFmpeg patch.
 * Compiles/executes the actual upstream libraries. It is not a subtitle renderer
 * and does not stand in for libmpv cache, GPU, or Mani window validation.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include "libavformat/avformat.h"
#include "libavcodec/avcodec.h"
#include "libavutil/error.h"

static void emit_error(const char *stage, int code)
{
    char message[AV_ERROR_MAX_STRING_SIZE];
    av_strerror(code, message, sizeof(message));
    fprintf(stderr, "%s: %s (%d)\n", stage, message, code);
    printf("{\"event\":\"error\",\"stage\":\"%s\",\"code\":%d}\n", stage, code);
}

static void describe_sub(const char *phase, int step, double target, AVSubtitle *sub)
{
    /* Test digest over every decoded bitmap row and palette; excludes padding.
     * This tests decoder output, not GPU rendering or screenshot pixels. */
    uint64_t bitmap = UINT64_C(14695981039346656037);
    for (unsigned i = 0; i < sub->num_rects; i++) {
        AVSubtitleRect *rect = sub->rects[i];
        if (rect->type != SUBTITLE_BITMAP) continue;
        for (int y = 0; y < rect->h; y++)
            for (int x = 0; x < rect->w; x++)
                bitmap = (bitmap ^ rect->data[0][y * rect->linesize[0] + x]) * UINT64_C(1099511628211);
        for (int j = 0; j < rect->nb_colors * 4; j++)
            bitmap = (bitmap ^ rect->data[1][j]) * UINT64_C(1099511628211);
    }
    printf("{\"event\":\"subtitle\",\"phase\":\"%s\",\"step\":%d,"
           "\"target\":%.3f,\"pts\":%lld,\"rects\":%u,\"x\":%d,\"y\":%d,"
           "\"width\":%d,\"height\":%d,\"bitmapDigest\":\"%016llx\"}\n",
           phase, step, target, (long long)sub->pts, sub->num_rects,
           sub->num_rects ? sub->rects[0]->x : -1,
           sub->num_rects ? sub->rects[0]->y : -1,
           sub->num_rects ? sub->rects[0]->w : 0,
           sub->num_rects ? sub->rects[0]->h : 0, (unsigned long long)bitmap);
}

static int decode(AVCodecContext *codec, AVPacket *packet, const char *phase,
                  int step, double target)
{
    AVSubtitle sub = {0};
    int got = 0, ret = avcodec_decode_subtitle2(codec, &sub, &got, packet);
    if (ret < 0)
        emit_error("decode", ret);
    else if (got)
        describe_sub(phase, step, target, &sub);
    avsubtitle_free(&sub);
    return ret;
}

int main(int argc, char **argv)
{
    AVFormatContext *format = NULL;
    AVCodecContext *codec = NULL;
    AVPacket *packet = NULL, *last_end = NULL;
    int ret, stream, sequence = 0;
    const AVCodec *decoder;
    const double targets[] = {2, 5, 2, 3.5, 7.5};
    if (argc != 2) return 2;
    av_log_set_level(AV_LOG_WARNING);
    ret = avformat_open_input(&format, argv[1], NULL, NULL);
    if (ret < 0) { emit_error("open", ret); return 3; }
    ret = avformat_find_stream_info(format, NULL);
    if (ret < 0) { emit_error("info", ret); avformat_close_input(&format); return 4; }
    stream = av_find_best_stream(format, AVMEDIA_TYPE_SUBTITLE, -1, -1, &decoder, 0);
    if (stream < 0) { emit_error("stream", stream); avformat_close_input(&format); return 5; }
    codec = avcodec_alloc_context3(decoder);
    if (!codec) return 6;
    avcodec_parameters_to_context(codec, format->streams[stream]->codecpar);
    codec->pkt_timebase = format->streams[stream]->time_base;
    ret = avcodec_open2(codec, decoder, NULL);
    if (ret < 0) { emit_error("codec", ret); return 7; }
    packet = av_packet_alloc();
    if (!packet) return 8;
    printf("{\"event\":\"identity\",\"avformat\":%u,\"avcodec\":%u,"
           "\"format\":\"%s\"}\n", avformat_version(), avcodec_version(), format->iformat->name);
    while ((ret = av_read_frame(format, packet)) >= 0) {
        if (packet->stream_index == stream) {
            printf("{\"event\":\"packet\",\"sequence\":%d,\"pos\":%lld,"
                   "\"pts\":%lld,\"dts\":%lld,\"key\":%d,\"type\":%d,\"size\":%d}\n",
                   sequence++, (long long)packet->pos, (long long)packet->pts,
                   (long long)packet->dts, !!(packet->flags & AV_PKT_FLAG_KEY),
                   packet->size ? packet->data[0] : -1, packet->size);
            decode(codec, packet, "initial", 0, 0);
            if (packet->size == 3 && packet->data[0] == 0x80) {
                av_packet_free(&last_end);
                last_end = av_packet_clone(packet);
            }
        }
        av_packet_unref(packet);
    }
    if (ret != AVERROR_EOF) emit_error("read", ret);
    if (last_end) {
        /* A bare END after flush must not reuse the previous presentation. */
        avcodec_flush_buffers(codec);
        decode(codec, last_end, "flush-end", 0, 0);
    }
    for (int step = 0; step < sizeof(targets)/sizeof(targets[0]); step++) {
        double target = targets[step];
        int64_t ts = (int64_t)(target / av_q2d(format->streams[stream]->time_base));
        ret = av_seek_frame(format, stream, ts, AVSEEK_FLAG_BACKWARD);
        if (ret < 0) { emit_error("seek", ret); continue; }
        avcodec_flush_buffers(codec);
        int first = 1;
        while ((ret = av_read_frame(format, packet)) >= 0) {
            if (packet->stream_index != stream) { av_packet_unref(packet); continue; }
            if (first) {
                printf("{\"event\":\"seek-start\",\"step\":%d,\"target\":%.3f,"
                       "\"pos\":%lld,\"key\":%d,\"type\":%d}\n",
                       step + 1, target, (long long)packet->pos,
                       !!(packet->flags & AV_PKT_FLAG_KEY),
                       packet->size ? packet->data[0] : -1);
                first = 0;
            }
            if (packet->pts != AV_NOPTS_VALUE &&
                packet->pts * av_q2d(format->streams[stream]->time_base) > target) {
                av_packet_unref(packet);
                break;
            }
            decode(codec, packet, "seek", step + 1, target);
            av_packet_unref(packet);
        }
    }
    av_packet_free(&last_end);
    av_packet_free(&packet);
    avcodec_free_context(&codec);
    avformat_close_input(&format);
    return 0;
}
