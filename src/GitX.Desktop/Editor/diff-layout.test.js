import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildAlignment, sourceLines, visualRow, nextChange, unchangedRegions, visibleAnchor } from './diff-layout.js';

test('file navigation detects both boundaries without wrapping a manually placed cursor',()=>{
  const blocks=[{visualStart:5},{visualStart:20}];
  assert.equal(nextChange(blocks,1,1,null,false),-1);
  assert.equal(nextChange(blocks,0,-1,null,false),-1);
  assert.equal(nextChange(blocks,-1,1,25,false),-1);
  assert.equal(nextChange(blocks,-1,-1,2,false),-1);
  assert.equal(nextChange(blocks,-1,1,10,false),1);
  assert.equal(nextChange(blocks,-1,-1,10,false),0);
  assert.equal(nextChange(blocks,-1,-1,null,false),1);
});

test('unequal replacements use view zones and leave every source line untouched', () => {
  const plan = buildAlignment({ old: { text: 'a\nb\nc\nd\ne\nf\n' }, new: { text: 'a\nx\ny\nz\nd\nf\n' }, blocks: [
    { oldStart: 1, oldCount: 2, newStart: 1, newCount: 3 }, { oldStart: 4, oldCount: 1, newStart: 5, newCount: 0 }
  ] });
  assert.deepEqual(plan.oldLines, ['a','b','c','d','e','f']);
  assert.deepEqual(plan.newLines, ['a','x','y','z','d','f']);
  assert.deepEqual(plan.oldZones, [{ afterLineNumber: 3, height: 1, index: 0 }]);
  assert.deepEqual(plan.newZones, [{ afterLineNumber: 5, height: 1, index: 1 }]);
  assert.equal(visualRow(4, plan.oldZones), visualRow(5, plan.newZones)); // d
  assert.equal(visualRow(6, plan.oldZones), visualRow(6, plan.newZones)); // f
  assert.deepEqual(plan.blocks.map(b => b.visualStart), [1,5]);
});
test('leading insertion aligns old line 1 with the correct new line', () => {
  const plan = buildAlignment({ old: { text: 'keep\n' }, new: { text: 'inserted\nkeep\n' }, blocks: [{ oldStart: 0, oldCount: 0, newStart: 0, newCount: 1 }] });
  assert.equal(visualRow(1, plan.oldZones), visualRow(2, plan.newZones));
  assert.equal(plan.oldZones[0].afterLineNumber, 0);
});
test('native line pairing places a gap before a modified line within a replacement', () => {
  const plan = buildAlignment({old:{text:'keep\nreturn 1;\nend\n'},new:{text:'keep\nif(x) return;\nreturn 2;\nend\n'},
    blocks:[{oldStart:1,oldCount:1,newStart:1,newCount:2,rows:[{oldLine:null,newLine:2},{oldLine:2,newLine:3}]}]});
  assert.deepEqual(plan.oldZones,[{afterLineNumber:1,height:1,index:0}]);
  assert.equal(visualRow(2,plan.oldZones),visualRow(3,plan.newZones));
  assert.equal(visualRow(3,plan.oldZones),visualRow(4,plan.newZones));
});
test('empty and deleted sides have no numbered placeholder source line', () => {
  for (const deletion of [false,true]) {
    const plan = buildAlignment({ old: { text: deletion ? 'a\nb\n' : '' }, new: { text: deletion ? '' : 'a\nb\n' },
      blocks: [{ oldStart: 0, oldCount: deletion ? 2 : 0, newStart: 0, newCount: deletion ? 0 : 2 }] });
    assert.equal((deletion ? plan.newLines : plan.oldLines).length, 0);
    const height = (lines,zones) => Math.max(1,lines.length) + zones.reduce((sum,z)=>sum+z.height,0);
    assert.equal(height(plan.oldLines,plan.oldZones),height(plan.newLines,plan.newZones));
  }
  assert.deepEqual(sourceLines('a\r\n\r\n'), ['a','']);
  assert.deepEqual(sourceLines(''), []);
});
test('F7 and Shift+F7 start at either end, wrap, and respect manually placed cursors', () => {
  const blocks = [{visualStart:0},{visualStart:10},{visualStart:30}];
  assert.equal(nextChange(blocks,-1,1),0);
  assert.equal(nextChange(blocks,-1,-1),2);
  assert.equal(nextChange(blocks,0,1),1);
  assert.equal(nextChange(blocks,2,1),0);
  assert.equal(nextChange(blocks,0,-1),2);
  assert.equal(nextChange(blocks,-1,1,12),2);
  assert.equal(nextChange(blocks,-1,-1,12),1);
  assert.equal(nextChange([], -1, 1),-1);
});

function contextFixture() {
  const oldLines = Array.from({length: 180}, (_, i) => `source ${i + 1}`);
  const newLines = [...oldLines];
  newLines.splice(139, 1, 'changed 140');
  newLines.splice(79, 2);
  newLines.splice(19, 1, 'inserted 1', 'inserted 2', 'changed 20');
  return buildAlignment({ old: {text: oldLines.join('\r\n') + '\r\n'}, new: {text: newLines.join('\n')}, blocks: [
    { oldStart:19,oldCount:1,newStart:19,newCount:3,rows:[{oldLine:null,newLine:20},{oldLine:null,newLine:21},{oldLine:20,newLine:22}] },
    { oldStart:79,oldCount:2,newStart:81,newCount:0 },
    { oldStart:139,oldCount:1,newStart:139,newCount:1 }
  ]});
}

test('unchanged folds preserve three context lines at file edges and around unequal blocks', () => {
  const plan = contextFixture();
  const regions = unchangedRegions(plan);
  assert.deepEqual(regions, [
    {id:0,count:13,oldStart:4,newStart:4},
    {id:1,count:53,oldStart:24,newStart:26},
    {id:2,count:52,oldStart:85,newStart:85},
    {id:3,count:34,oldStart:144,newStart:144}
  ]);
  for (const region of regions) {
    const oldSlice = plan.oldLines.slice(region.oldStart - 1, region.oldStart - 1 + region.count);
    const newSlice = plan.newLines.slice(region.newStart - 1, region.newStart - 1 + region.count);
    assert.deepEqual(oldSlice, newSlice); // Only equal source content may be hidden.
    for (const block of plan.blocks) for (const row of block.rows) {
      assert.ok(row.oldLine === null || row.oldLine < region.oldStart || row.oldLine >= region.oldStart + region.count);
      assert.ok(row.newLine === null || row.newLine < region.newStart || row.newLine >= region.newStart + region.count);
    }
  }
});

test('folded line geometry remains aligned after expanding any individual region', () => {
  const plan = contextFixture(), regions = unchangedRegions(plan);
  for (const opened of [-1,0,1,2,3,4]) {
    const collapsed = new Set(regions.filter(r => opened !== 4 && r.id !== opened).map(r => r.id));
    // Monaco removes hidden source rows, and each region has one shared control zone.
    const screenRow = (line, side) => {
      let row = visualRow(line, side === 0 ? plan.oldZones : plan.newZones);
      for (const region of regions) {
        const start = side === 0 ? region.oldStart : region.newStart;
        if (line >= start) row++;
        if (collapsed.has(region.id) && line >= start + region.count) row -= region.count;
      }
      return row;
    };
    for (const block of plan.blocks) for (const row of block.rows)
      if (row.oldLine && row.newLine) assert.equal(screenRow(row.oldLine,0),screenRow(row.newLine,1));
    // Equal context after an insertion, after a deletion and at EOF.
    for (const [oldLine,newLine] of [[21,23],[82,82],[180,180]])
      assert.equal(screenRow(oldLine,0),screenRow(newLine,1));
  }
});

test('F7 order uses source coordinates regardless of folding; collapsed cursors anchor on the correct side', () => {
  const plan = contextFixture(), regions = unchangedRegions(plan), collapsed = new Set(regions.map(r=>r.id));
  assert.equal(visibleAnchor(45,0,regions,collapsed),23);
  assert.equal(visibleAnchor(47,1,regions,collapsed),25);
  assert.equal(visibleAnchor(140,1,regions,collapsed),140); // changed line never hidden
  assert.equal(visibleAnchor(45,0,regions,new Set()),45);
  for (const [line,side] of [[23,0],[25,1],[45,0],[47,1]]) {
    const row = visualRow(line,side===0?plan.oldZones:plan.newZones);
    assert.equal(nextChange(plan.blocks,-1,1,row),1); // pure deletion
    assert.equal(nextChange(plan.blocks,-1,-1,row),0);
  }
  assert.equal(nextChange(plan.blocks,2,1),0);
  assert.equal(nextChange(plan.blocks,0,-1),2);
});

test('short context, identical text, empty sides and edge-only insertions do not hide changes', () => {
  const text = Array.from({length:20},(_,i)=>String(i)).join('\n');
  assert.deepEqual(unchangedRegions(buildAlignment({old:{text},new:{text},blocks:[]})),[]);
  for (const deletion of [false,true]) assert.deepEqual(unchangedRegions(buildAlignment({
    old:{text:deletion?text:''},new:{text:deletion?'':text},
    blocks:[{oldStart:0,oldCount:deletion?20:0,newStart:0,newCount:deletion?0:20}]
  })),[]);
  const plan = buildAlignment({old:{text:'a\nb\nc\nd\ne\nf\ng\nh\ni'},new:{text:'added\na\nb\nc\nd\ne\nf\ng\nh\ni\nlast'},
    blocks:[{oldStart:0,oldCount:0,newStart:0,newCount:1},{oldStart:9,oldCount:0,newStart:10,newCount:1}]});
  assert.deepEqual(unchangedRegions(plan),[]); // nine context rows: not worth a fold
});
