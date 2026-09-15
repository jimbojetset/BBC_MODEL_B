// Runs the original Chris Evans video tests; upstream video.c includes test-video.c.
#include "video.c"
#include <stdio.h>
#include <stdlib.h>
void via_set_CA1(struct via_struct* via, int level) { (void)via; (void)level; abort(); }
void via_set_CB2_changed_callback(struct via_struct* v, void (*f)(void*, int, int), void* p) { (void)v; (void)f; (void)p; abort(); }
void via_set_PCR_changed_callback(struct via_struct* v, void (*f)(void*, uint8_t), void* p) { (void)v; (void)f; (void)p; abort(); }
int main(void) {
 video_test_init(); video_test_6845_corner_cases(); video_test_end(); puts("corner cases PASS");
 video_test_init(); video_test_no_dummy_raster(); video_test_end(); puts("no dummy raster PASS");
 video_test_init(); video_test_vsync_mux(); video_test_end(); puts("VSYNC mux PASS");
 return 0;
}
