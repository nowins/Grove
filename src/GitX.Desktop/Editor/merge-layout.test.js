import test from 'node:test';
import assert from 'node:assert/strict';
import {threeWayRows,alignmentZones,sourceLines} from './merge-layout.js';
test('three columns join unequal native hunks without adding source lines',()=>{
  const left={old:{text:'a\nx\nb\n'},new:{text:'a\nb\n'},blocks:[{oldStart:1,oldCount:1,newStart:1,newCount:0,rows:[{oldLine:2,newLine:null}]}]};
  const right={old:{text:'a\ny\nz\nb\n'},new:{text:'a\nb\n'},blocks:[{oldStart:1,oldCount:2,newStart:1,newCount:0,rows:[{oldLine:2,newLine:null},{oldLine:3,newLine:null}]}]};
  const rows=threeWayRows(left,right); assert.deepEqual(rows,[[1,1,1],[2,null,2],[null,null,3],[3,2,4]]);
  const zones=alignmentZones(rows,[3,3,4]); const heights=zones.map((items,i)=>[3,3,4][i]+items.reduce((s,z)=>s+z.height,0));
  assert.equal(new Set(heights).size,1);
});
test('empty result and a fully deleted side retain every source line exactly once',()=>{
  const diff={old:{text:'a\nb'},new:{text:''},blocks:[{oldStart:0,oldCount:2,newStart:0,newCount:0,rows:[{oldLine:1,newLine:null},{oldLine:2,newLine:null}]}]};
  assert.deepEqual(threeWayRows(diff,{old:{text:''},new:{text:''},blocks:[]}),[[1,null,null],[2,null,null]]);
});
test('conflict colors and gutter actions include inserted source lines but exclude the next result line',()=>{
  const rows=[[1,1,1],[2,null,2],[3,2,3],[4,3,4],[5,null,null]];
  assert.deepEqual(sourceLines(rows,0,2,2),[2,3]);
  assert.deepEqual(sourceLines(rows,2,2,2,true),[2]);
  assert.deepEqual(sourceLines(rows,0,4,4,true),[5]);
});
