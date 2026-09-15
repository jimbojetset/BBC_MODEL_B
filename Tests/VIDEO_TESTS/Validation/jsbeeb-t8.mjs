// Replay of our T8 adapter against pinned jsbeeb, with ABGR framebuffer colours.
import { Video } from './src/video.js';
import assert from 'node:assert/strict';
for (const phase of [false,true]) {
 const mem=new Uint8Array(65536), pixels=new Uint32Array(1024*625);
 const v=new Video(true,pixels,()=>{}); v.reset({videoRead:a=>mem[a]}, {setVBlankInt:()=>{}});
 const crtc=(r,b)=>{v.crtc.write(0,r);v.crtc.write(1,b)};
 [63,40,51,0x24,30,2,25,28,0x93,18,0x72,0x13,0,0,0,0].forEach((b,r)=>crtc(r,b));
 v.ula.write(0,2);
 for(const [r,b] of [[4,38],[5,0],[6,26],[7,35],[8,0],[9,7],[10,0x20],[12,0x28],[13,0]])crtc(r,b);
 v.ula.write(0,0x88);v.ula.write(1,0xf6);mem.fill(255,0x7c00,0x8000);v.polltime(80000);
 let found=false;
 for(let n=0;n<160000;n++) {
  if(v.vertCounter===10&&v.scanlineCounter===3&&v.horizCounter===4&&v.oddClock===phase){found=true;break;}
  v.polltime(1);
 }
 assert(found);const startX=v.bitmapX,y=v.bitmapY;
 for(let i=0;i<6;i++){v.ula.write(0,i%2?0x88:0x8a);v.polltime(11)}
 v.polltime(8);const boxes=[],to=startX+6*11*8+16;
 for(let x=startX-16;x<to;){if(pixels[y*1024+x]===0xff0000ff){x++;continue}const start=x;while(x<to&&pixels[y*1024+x]!==0xff0000ff)x++;boxes.push([start,x-start]);}
 assert.equal(boxes.length,3);for(const b of boxes)assert.equal(b[1],88);
 for(let i=1;i<boxes.length;i++)assert.equal(boxes[i][0]-boxes[i-1][0]-boxes[i-1][1],88);
 console.log(`T8 phase ${Number(phase)} PASS: widths ${boxes.map(b=>b[1])}`);
}
