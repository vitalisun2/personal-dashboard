(()=>{
const root=document.getElementById('kb-fixed-drop');if(!root||root.dataset.ready)return;root.dataset.ready='1';
const tree=root.querySelector('#treeFX'),results=root.querySelector('#resultsFX'),search=root.querySelector('#searchFX'),clear=root.querySelector('#clearFX'),knowledge=root.querySelector('#knowledgeFX'),docView=root.querySelector('#docFX'),placeholder=root.querySelector('#placeholderFX'),title=root.querySelector('#titleFX'),back=root.querySelector('#backFX'),scroll=root.querySelector('#scrollFX'),nav=root.querySelector('#bottomNav'),toggleAllKnowledge=root.querySelector('#toggleKnowledgeSectionsFX'),orderKnowledge=root.querySelector('#knowledgeOrderFX');
const del=root.querySelector('#deleteFX'),docTitle=root.querySelector('#docTitleFX'),docTitleInput=root.querySelector('#docTitleInputFX'),docContent=root.querySelector('#docContentFX'),docContentInput=root.querySelector('#docContentInputFX');
const overlay=root.querySelector('#overlayFX'),sheet=root.querySelector('#createFX'),parentList=root.querySelector('#parentListFX');
const contextMenu=window.personalOSContextMenu;
const state={page:'knowledge',expanded:new Set(['lch','engine','art','ui','architecture','release']),query:'',returnMode:'tree',createType:'document',parentId:'',revealed:null,currentDocId:'',bodyEditing:false,titleEditing:false,orderMode:false};
const D=(id,title,content,tags)=>({id,type:'document',title,content,tags});
const docs=[D('bg','Background assets','Background Paris, background New York и требования к широким background-текстурам.',['background','art']),D('spawn','Переделка Spawn Engine под расширенные фоны','Переделать генератор сегментов под фоны увеличенной длины. Окно спауна учитывает двойную ширину секций и уровни в два раза длиннее.',['background','spawn','engine','double']),D('width','Миграция уровней на увеличенную ширину','После удвоения длины level chunks пересчитать позиции препятствий и переходы между сегментами.',['spawn','engine','double']),D('cache','Background cache','Кэширование background-ресурсов между сегментами уровня.',['background','engine']),D('decor','Декор локаций','Фонари, растения, вывески, скамейки и оформление городских локаций.',['art','background']),D('menu','Главное меню','Навигация главного меню, Shop, Hero и Quests.',['ui']),D('character','Character Development','Скины, способности и прогресс игрока.',['ui']),D('search','Архитектура Hybrid Search','Текстовый поиск, vector search, score и reranker.',['search','architecture']),D('agent','AI Agent','Инструменты Knowledge, Planning и Tasks.',['agent','architecture']),D('storage','Storage','PostgreSQL и локальное offline-first хранение.',['storage']),D('offline','Offline Sync','Синхронизация локальных изменений после восстановления сети.',['storage']),D('release','Soft Launch checklist','Проверка UI, аналитики, экономики, арта и аудио.',['release'])];
let nodes=[{id:'lch',type:'section',title:'Lost Cyber Hamster',children:[{id:'engine',type:'section',title:'Level Engine',children:[docs[1],docs[2],docs[3]]},{id:'art',type:'section',title:'Арт и окружение',children:[docs[0],docs[4]]},{id:'ui',type:'section',title:'UI / UX',children:[docs[5],docs[6]]}]},{id:'architecture',type:'section',title:'Architecture',children:[docs[7],docs[8],docs[9],docs[10]]},{id:'release',type:'section',title:'Release',children:[docs[11]]}];
function dots(){return '<span>'+Array.from({length:6},()=>'<i></i>').join('')+'</span>'}
function pencil(){return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20l4.2-1 10.3-10.3a2.1 2.1 0 0 0-3-3L5.2 16 4 20Z"></path><path d="m13.8 7.2 3 3"></path></svg>'}
function trash(){return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16"></path><path d="M9 7V4h6v3"></path><path d="m6 7 1 13h10l1-13"></path><path d="M10 11v5M14 11v5"></path></svg>'}
function find(id,list=nodes,path=[],parent=null){for(let i=0;i<list.length;i++){const n=list[i],p=[...path,n.title];if(n.id===id)return{node:n,list,index:i,path:p,parent};if(n.children){const r=find(id,n.children,p,n);if(r)return r}}return null}
function remove(id,list=nodes){for(let i=0;i<list.length;i++){if(list[i].id===id)return list.splice(i,1)[0];if(list[i].children){const r=remove(id,list[i].children);if(r)return r}}return null}
function descendant(n,id){return !!n.children?.some(c=>c.id===id||descendant(c,id))}
function sections(list=nodes,out=[],depth=0){for(const n of list){if(n.type==='section'){out.push({node:n,depth});sections(n.children||[],out,depth+1)}}return out}
function allDocs(list=nodes,path=[],out=[]){for(const n of list){const p=[...path,n.title];if(n.type==='document')out.push({node:n,path:p});else allDocs(n.children||[],p,out)}return out}
function collapseIcon(inward){const upper=inward?'M5 3 10 8 15 3':'M5 8 10 3 15 8',lower=inward?'M5 17 10 12 15 17':'M5 12 10 17 15 12';return `<svg viewBox="0 0 20 20" aria-hidden="true"><path d="${upper}"/><path d="${lower}"/></svg>`}
function toggle(id){if(state.orderMode)return;if(state.revealed){closeSwipe();return}state.expanded.has(id)?state.expanded.delete(id):state.expanded.add(id);renderTree()}
function closeSwipe(){contextMenu.close();state.revealed=null}
function showMenu(id,wrap,row,point=null,trigger=null){
  const info=find(id);if(!info||state.orderMode)return;
  const type=info.node.type==='section'?'раздел':'документ';
  const items=[
    {label:'Переименовать',action:()=>renameNode(id)},
    {label:type==='раздел'?'Удалить раздел':'Удалить документ',danger:true,action:()=>{
      const current=find(id);if(!current)return;
      contextMenu.confirmAction({title:'Удалить '+type+' «'+current.node.title+'»?',body:type==='раздел'?'Все документы внутри раздела тоже будут удалены.':'Документ будет удалён из базы знаний.',confirmLabel:'Удалить',onConfirm:()=>{
        remove(id);state.expanded.delete(id);renderTree();
      }});
    }}
  ];
  contextMenu.open({owner:wrap,anchor:row,items,point,trigger,
    label:'Действия с '+type+'ом «'+info.node.title+'»',
    onClose:()=>{state.revealed=null;}});
  state.revealed=id;
}
let swipe=null,suppressUntil=0;
function bindSwipe(wrap,row,id){
  const cancelTimer=()=>{if(swipe?.timer){clearTimeout(swipe.timer);swipe.timer=null}};
  row.addEventListener('pointerdown',e=>{
    if(state.orderMode||e.button>0||e.target.closest('.handle,.row-menu-trigger,.rename-input'))return;
    cancelTimer();
    const current={id,pid:e.pointerId,startX:e.clientX,startY:e.clientY,moved:false,long:false,timer:null};
    swipe=current;
    if(e.pointerType==='touch'||e.pointerType==='pen')current.timer=setTimeout(()=>{
      if(swipe!==current||current.moved)return;
      current.long=true;suppressUntil=Date.now()+350;showMenu(id,wrap,row);
    },480);
    e.target.setPointerCapture?.(e.pointerId);
  });
  row.addEventListener('pointermove',e=>{
    if(!swipe||swipe.id!==id||swipe.pid!==e.pointerId)return;
    const dx=e.clientX-swipe.startX,dy=e.clientY-swipe.startY;
    if(Math.hypot(dx,dy)>8){swipe.moved=true;cancelTimer()}
    if(Math.abs(dy)>Math.abs(dx)&&Math.abs(dy)>10){swipe=null;return}
    if(dx<-12&&Math.abs(dx)>Math.abs(dy)*1.1)e.preventDefault();
  },{passive:false});
  row.addEventListener('pointerup',e=>{
    if(!swipe||swipe.id!==id||swipe.pid!==e.pointerId)return;
    const current=swipe;cancelTimer();swipe=null;
    if(current.long)return;
    const dx=e.clientX-current.startX,dy=e.clientY-current.startY;
    if(Math.abs(dx)<Math.abs(dy)*1.2)return;
    if(dx<=-56){suppressUntil=Date.now()+320;showMenu(id,wrap,row)}
    else if(dx>=45&&contextMenu.isOpenFor(wrap)){suppressUntil=Date.now()+320;closeSwipe()}
  });
  row.addEventListener('pointercancel',e=>{if(swipe?.id===id&&swipe.pid===e.pointerId){cancelTimer();swipe=null}});
  row.addEventListener('contextmenu',e=>{
    e.preventDefault();if(state.orderMode||contextMenu.isOpenFor(wrap))return;
    showMenu(id,wrap,row,{x:e.clientX,y:e.clientY});
  });
}
function draw(list,depth=0,parent=tree){
  for(const n of list){
    const block=document.createElement('div'),wrap=document.createElement('div'),row=document.createElement('div');
    wrap.className='row-wrap';wrap.dataset.wrap=n.id;
    row.className='node '+n.type;row.dataset.id=n.id;row.style.paddingLeft=(depth*17)+'px';
    if(n.type==='section'){
      const c=document.createElement('button');c.type='button';c.className='chev '+(state.expanded.has(n.id)?'open':'');c.textContent='›';c.addEventListener('click',()=>toggle(n.id));row.append(c);
      const name=document.createElement('button');name.type='button';name.className='section-name';name.textContent=n.title;name.addEventListener('click',()=>toggle(n.id));row.append(name);
    }else{
      const name=document.createElement('button');name.type='button';name.className='doc-title';name.textContent=n.title;
      name.addEventListener('click',()=>{if(state.orderMode)return;state.returnMode=state.query?'search':'tree';openDoc(n)});row.append(name);
    }
    const menuButton=document.createElement('button');menuButton.type='button';menuButton.className='row-menu-trigger';menuButton.textContent='⋯';
    menuButton.setAttribute('aria-label','Действия с '+n.title);menuButton.setAttribute('aria-haspopup','menu');menuButton.setAttribute('aria-expanded','false');menuButton.title='Действия';menuButton.hidden=state.orderMode;
    menuButton.addEventListener('click',e=>{e.stopPropagation();contextMenu.isOpenFor(wrap)?closeSwipe():showMenu(n.id,wrap,row,null,menuButton)});
    row.append(menuButton);
    const h=document.createElement('button');h.type='button';h.className='handle';h.dataset.drag=n.id;h.innerHTML=dots();h.setAttribute('aria-label','Перетащить '+n.title);h.hidden=!state.orderMode;row.append(h);
    wrap.append(row);block.append(wrap);parent.append(block);bindSwipe(wrap,row,n.id);
    if(n.type==='section'&&state.expanded.has(n.id)){const child=document.createElement('div');block.append(child);draw(n.children||[],depth+1,child)}
  }
}
function renderTree(){tree.innerHTML='';draw(nodes);bindDrag();orderKnowledge.setAttribute('aria-pressed',String(state.orderMode));orderKnowledge.setAttribute('aria-label',state.orderMode?'Выключить сортировку':'Включить сортировку');orderKnowledge.title=state.orderMode?'Выключить сортировку':'Включить сортировку';orderKnowledge.disabled=Boolean(state.query);const ids=sections().map(x=>x.node.id),allOpen=ids.length>0&&ids.every(id=>state.expanded.has(id)),label=allOpen?'Свернуть все разделы':'Развернуть все разделы';toggleAllKnowledge.innerHTML=collapseIcon(allOpen);toggleAllKnowledge.title=label;toggleAllKnowledge.setAttribute('aria-label',label)}
function renameNode(id){const info=find(id);const wrap=root.querySelector(`[data-wrap="${id}"]`);if(!info||!wrap)return;closeSwipe();const row=wrap.querySelector('[data-id]');row.innerHTML='';const input=document.createElement('input');input.className='rename-input';input.value=info.node.title;row.append(input);let done=false;const finish=save=>{if(done)return;done=true;const value=input.value.trim();if(save&&value)info.node.title=value;renderTree()};input.addEventListener('keydown',e=>{if(e.key==='Enter')finish(true);if(e.key==='Escape')finish(false)});input.addEventListener('blur',()=>finish(true));setTimeout(()=>{input.focus();input.select()},20)}
root.addEventListener('click',e=>{if(Date.now()<suppressUntil&&!e.target.closest('.row-context-menu')){e.preventDefault();e.stopPropagation()}},true);
const stop=new Set(['где','что','как','мы','про','это','там','под','для','вот','когда','найди','поиск','делали','было']);
function words(q){return q.toLowerCase().replace(/[.,!?;:()]/g,' ').split(/\s+/).filter(Boolean)}
function exactScore(doc,q){const hay=(doc.title+' '+doc.content).toLowerCase(),raw=q.toLowerCase();let s=hay.includes(raw)?100:0;for(const w of words(q)){if(w.length>=3&&!stop.has(w)&&hay.includes(w))s+=5}return s}
function concepts(q){const s=q.toLowerCase(),o=new Set();const add=(k,a)=>{if(a.some(x=>s.includes(x)))o.add(k)};add('background',['background','бэкграунд','фон','фоны']);add('spawn',['spawn','спаун','спавн','движок','генератор']);add('engine',['engine','движок','генератор']);add('double',['в два раза','двойн','удво']);return o}
function semanticScore(doc,q){const c=concepts(q);let s=0;c.forEach(x=>{if(doc.tags?.includes(x))s+=10});if(c.has('background')&&c.has('spawn')&&doc.tags?.includes('background')&&doc.tags?.includes('spawn'))s+=15;return s}
function classify(q){const all=allDocs();const exact=all.map(x=>({...x,score:exactScore(x.node,q)})).filter(x=>x.score>0).sort((a,b)=>b.score-a.score);const ids=new Set(exact.map(x=>x.node.id));const semantic=all.map(x=>({...x,score:semanticScore(x.node,q)})).filter(x=>x.score>0&&!ids.has(x.node.id)).sort((a,b)=>b.score-a.score);return{exact,semantic,long:words(q).length>=4||q.length>28}}
function esc(s){return String(s).replace(/[&<>\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;'}[c]||c))}
function rx(s){return String(s).replace(/[.*+?^${}()|[\]\\]/g,'\\$&')}
function highlight(text,q){let out=esc(text);for(const t of words(q).filter(x=>x.length>=3&&!stop.has(x)))out=out.replace(new RegExp('('+rx(t)+')','ig'),'<mark class="exact">$1</mark>');return out}
function cards(items,q,exact){return items.map(x=>`<button type="button" class="result" data-result="${x.node.id}"><div class="result-title">${exact?highlight(x.node.title,q):esc(x.node.title)}</div><div class="result-path">${x.path.slice(0,-1).join(' › ')}</div><div class="result-snippet">${exact?highlight(x.node.content,q):esc(x.node.content)}</div></button>`).join('')}
function group(label,kind,items,q,exact){if(!items.length)return'';return `<section class="group ${kind}"><div class="group-head"><span class="group-name"><span class="kind">${exact?'Aa':'≈'}</span>${label}</span><span>${items.length}</span></div>${cards(items,q,exact)}</section>`}
function showResults(q){closeSwipe();state.orderMode=false;state.query=q;root.querySelector('#knowledgeFX .toolbar').classList.toggle('search-expanded',Boolean(q));renderTree();const r=classify(q);tree.hidden=true;results.hidden=false;clear.classList.add('show');const a=r.long?group('По смыслу','semantic',r.semantic,q,false):group('Точные совпадения','exact',r.exact,q,true);const b=r.long?group('Точные совпадения','exact',r.exact,q,true):group('По смыслу','semantic',r.semantic,q,false);results.innerHTML=(a+b)||'<div class="empty">Ничего не найдено</div>';results.querySelectorAll('[data-result]').forEach(b=>b.addEventListener('click',()=>{state.returnMode='search';openDoc(find(b.dataset.result).node)}));scroll.scrollTop=0}
function resetSearch(){state.query='';root.querySelector('#knowledgeFX .toolbar').classList.remove('search-expanded');orderKnowledge.disabled=false;tree.hidden=false;results.hidden=true;clear.classList.remove('show');results.innerHTML='';scroll.scrollTop=0}
search.addEventListener('input',()=>search.value.trim()?showResults(search.value.trim()):resetSearch());
search.addEventListener('blur',()=>{if(!search.value.trim())root.querySelector('#knowledgeFX .toolbar').classList.remove('search-expanded')});
toggleAllKnowledge.addEventListener('click',()=>{const ids=sections().map(x=>x.node.id),allOpen=ids.length>0&&ids.every(id=>state.expanded.has(id));if(allOpen)ids.forEach(id=>state.expanded.delete(id));else ids.forEach(id=>state.expanded.add(id));renderTree()});
orderKnowledge.addEventListener('click',()=>{if(state.query)return;closeSwipe();swipe=null;clearGhost();clearDrop();drag=null;state.orderMode=!state.orderMode;renderTree()});
clear.addEventListener('click',()=>{search.value='';resetSearch();search.focus()});
function currentDoc(){return state.currentDocId?find(state.currentDocId)?.node:null}
function syncDocView(){const n=currentDoc();if(!n)return;docTitle.textContent=n.title;docContent.textContent=n.content||'Новый документ. Содержимое пока пустое.';docTitleInput.value=n.title;docContentInput.value=n.content||'';docTitle.hidden=state.titleEditing;docTitleInput.hidden=!state.titleEditing;docContent.hidden=state.bodyEditing;docContentInput.hidden=!state.bodyEditing}
function finishTitleEdit(save=true){if(!state.titleEditing)return;const n=currentDoc();if(save&&n){const next=docTitleInput.value.trim();if(next)n.title=next}state.titleEditing=false;syncDocView()}
function startTitleEdit(){const n=currentDoc();if(!n||state.titleEditing)return;finishBodyEdit();state.titleEditing=true;syncDocView();docTitleInput.focus();docTitleInput.select()}
function finishBodyEdit(save=true){if(!state.bodyEditing)return;const n=currentDoc();if(save&&n)n.content=docContentInput.value;state.bodyEditing=false;syncDocView()}
function startBodyEdit(){const n=currentDoc();if(!n||state.bodyEditing)return;finishTitleEdit();state.bodyEditing=true;syncDocView();docContentInput.focus()}
function finishDocEdits(){finishTitleEdit();finishBodyEdit()}
function openDoc(n){finishDocEdits();state.orderMode=false;state.currentDocId=n.id;state.bodyEditing=false;state.titleEditing=false;knowledge.hidden=true;placeholder.hidden=true;docView.hidden=false;title.textContent='База знаний';syncDocView();docView.scrollTop=0}
function closeDoc(){finishTitleEdit();finishBodyEdit();state.currentDocId='';docView.hidden=true;knowledge.hidden=false;title.textContent='База знаний';renderTree();if(state.returnMode==='search'&&state.query){search.value=state.query;showResults(state.query)}else resetSearch();state.returnMode='tree'}
back.addEventListener('click',closeDoc);
docTitle.addEventListener('click',startTitleEdit);
docTitleInput.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();finishTitleEdit()}else if(e.key==='Escape'){e.preventDefault();finishTitleEdit(false)}});
docTitleInput.addEventListener('blur',()=>finishTitleEdit());
docContent.addEventListener('click',startBodyEdit);
docContentInput.addEventListener('keydown',e=>{if(e.key==='Escape'){e.preventDefault();finishBodyEdit(false)}});
docContentInput.addEventListener('blur',()=>finishBodyEdit());
del.addEventListener('click',()=>{const n=currentDoc();if(!n)return;contextMenu.confirmAction({title:'Удалить документ «'+n.title+'»?',body:'Документ будет удалён из базы знаний.',confirmLabel:'Удалить',onConfirm:()=>{remove(n.id);state.currentDocId='';state.bodyEditing=false;state.titleEditing=false;docView.hidden=true;knowledge.hidden=false;title.textContent='База знаний';renderTree();if(state.returnMode==='search'&&state.query){search.value=state.query;showResults(state.query)}else resetSearch();state.returnMode='tree'}})});
function openCreate(){closeSwipe();state.createType='document';state.parentId='';root.querySelector('#nameFX').value='';updateCreate();renderParents();parentList.classList.remove('open');overlay.classList.add('open');sheet.classList.add('open');setTimeout(()=>root.querySelector('#nameFX').focus({preventScroll:true}),30)}
function closeCreate(){sheet.classList.remove('open');overlay.classList.remove('open');parentList.classList.remove('open');root.querySelector('#nameFX').value='';state.parentId=''}
function updateCreate(){const d=state.createType==='document';root.querySelector('#typeDocFX').classList.toggle('active',d);root.querySelector('#typeSectionFX').classList.toggle('active',!d);root.querySelector('#createTitleFX').textContent=d?'Новый документ':'Новый раздел';root.querySelector('#nameFX').placeholder=d?'Название документа':'Название раздела'}
function renderParents(){root.querySelector('#whereValueFX').textContent=state.parentId?(find(state.parentId)?.node.title||'Верхний уровень'):'Верхний уровень';parentList.innerHTML='';const top=document.createElement('button');top.type='button';top.className='parent-option '+(!state.parentId?'selected':'');top.textContent='Верхний уровень';top.addEventListener('click',()=>selectParent(''));parentList.append(top);for(const s of sections()){const b=document.createElement('button');b.type='button';b.className='parent-option '+(state.parentId===s.node.id?'selected':'');b.style.paddingLeft=(9+s.depth*15)+'px';b.textContent=s.node.title;b.addEventListener('click',()=>selectParent(s.node.id));parentList.append(b)}}
function selectParent(id){state.parentId=id;renderParents();parentList.classList.remove('open')}
root.querySelector('#addFX').addEventListener('click',openCreate);
root.querySelector('#closeFX').addEventListener('click',closeCreate);
overlay.addEventListener('click',closeCreate);
root.querySelector('#typeDocFX').addEventListener('click',()=>{state.createType='document';updateCreate()});
root.querySelector('#typeSectionFX').addEventListener('click',()=>{state.createType='section';updateCreate()});
root.querySelector('#whereFX').addEventListener('click',()=>parentList.classList.toggle('open'));
root.querySelector('#submitFX').addEventListener('click',()=>{const name=root.querySelector('#nameFX').value.trim();if(!name)return;const node={id:'new'+Date.now(),type:state.createType,title:name,...(state.createType==='section'?{children:[]}:{content:'',tags:[]})};if(state.parentId){const p=find(state.parentId)?.node;if(p){p.children=p.children||[];p.children.push(node);state.expanded.add(p.id)}else nodes.push(node)}else nodes.push(node);const type=state.createType;closeCreate();resetSearch();renderTree();if(type==='document'){state.returnMode='tree';openDoc(node)}});
let drag=null;
function clearDrop(){root.querySelectorAll('.drop-inside,.drop-before,.drop-after').forEach(x=>x.classList.remove('drop-inside','drop-before','drop-after'))}
function clearGhost(){if(drag?.row)drag.row.classList.remove('drag-source');drag?.ghost?.remove()}
function bindDrag(){root.querySelectorAll('[data-drag]').forEach(h=>h.addEventListener('pointerdown',e=>{if(e.button!==0||!state.orderMode)return;e.preventDefault();const row=h.closest('[data-id]'),r=row.getBoundingClientRect();drag={id:h.dataset.drag,pid:e.pointerId,startX:e.clientX,startY:e.clientY,row,target:null,place:null,started:false,ghost:null,offsetX:e.clientX-r.left,offsetY:e.clientY-r.top,width:r.width,height:r.height};h.setPointerCapture?.(e.pointerId)}))}
root.addEventListener('pointermove',e=>{if(!drag||drag.pid!==e.pointerId)return;if(!drag.started&&Math.hypot(e.clientX-drag.startX,e.clientY-drag.startY)>6){drag.started=true;drag.row.classList.add('drag-source');const ghost=document.createElement('div');ghost.className='reorder-ghost';ghost.textContent=find(drag.id)?.node.title||drag.row.textContent;ghost.style.width=drag.width+'px';ghost.style.height=drag.height+'px';root.append(ghost);drag.ghost=ghost}if(!drag.started)return;e.preventDefault();const bounds=scroll.getBoundingClientRect(),pad=4;drag.ghost.style.left=Math.max(bounds.left+pad,Math.min(e.clientX-drag.offsetX,bounds.right-drag.width-pad))+'px';drag.ghost.style.top=Math.max(bounds.top+pad,Math.min(e.clientY-drag.offsetY,bounds.bottom-drag.height-pad))+'px';clearDrop();const hit=document.elementFromPoint(e.clientX,e.clientY)?.closest?.('[data-id]');drag.target=null;drag.place=null;if(!hit||hit.dataset.id===drag.id)return;const src=find(drag.id)?.node,tgt=find(hit.dataset.id)?.node;if(!src||!tgt||descendant(src,tgt.id))return;const rect=hit.getBoundingClientRect(),ratio=(e.clientY-rect.top)/Math.max(1,rect.height);let place;if(tgt.type==='section'){place=ratio<.2?'before':ratio>.8?'after':'inside'}else{place=ratio<.5?'before':'after'};hit.classList.add(place==='inside'?'drop-inside':place==='before'?'drop-before':'drop-after');drag.target=hit;drag.place=place},{passive:false});
root.addEventListener('pointerup',e=>{if(!drag||drag.pid!==e.pointerId)return;const d=drag;clearGhost();clearDrop();drag=null;if(!d.started||!d.target)return;const src=find(d.id)?.node,tgtId=d.target.dataset.id;if(!src||descendant(src,tgtId))return;const moved=remove(d.id),target=find(tgtId);if(!moved||!target){if(moved)nodes.push(moved);renderTree();return}if(d.place==='inside'&&target.node.type==='section'){target.node.children=target.node.children||[];target.node.children.push(moved);state.expanded.add(target.node.id)}else{target.list.splice(target.index+(d.place==='after'?1:0),0,moved)}renderTree()});
root.addEventListener('pointercancel',()=>{clearGhost();clearDrop();drag=null});
let navGesture=null,navFrame=0;
function navStep(){const items=[...nav.querySelectorAll('.bottom-item')];if(items.length<2)return nav.clientWidth/3;return items[1].getBoundingClientRect().left-items[0].getBoundingClientRect().left}
function animateNav(target){cancelAnimationFrame(navFrame);const start=nav.scrollLeft,d=target-start;if(Math.abs(d)<1){nav.scrollLeft=target;return}const t0=performance.now(),duration=Math.min(360,Math.max(220,Math.abs(d)*.55));const tick=now=>{const p=Math.min(1,(now-t0)/duration),ease=1-(1-p)**3;nav.scrollLeft=start+d*ease;if(p<1)navFrame=requestAnimationFrame(tick);else navFrame=0};navFrame=requestAnimationFrame(tick)}
function snapNav(vx=0){const step=navStep(),max=nav.scrollWidth-nav.clientWidth,projected=nav.scrollLeft-vx*180,target=Math.max(0,Math.min(max,Math.round(projected/step)*step));animateNav(target)}
nav.addEventListener('pointerdown',e=>{if(e.button!==0||navGesture)return;const btn=e.target.closest('.bottom-item');e.preventDefault();cancelAnimationFrame(navFrame);const g=navGesture={button:btn,pid:e.pointerId,startX:e.clientX,startY:e.clientY,startScroll:nav.scrollLeft,moved:false,dragging:false,armed:false,lastX:e.clientX,lastTime:e.timeStamp,vx:0,ghost:null,target:null,offsetX:0,offsetY:0,timer:null};g.timer=btn?setTimeout(()=>{if(navGesture===g)g.armed=true},200):null;nav.setPointerCapture?.(e.pointerId)});
nav.addEventListener('pointermove',e=>{const g=navGesture;if(!g||g.pid!==e.pointerId)return;const dx=e.clientX-g.startX,dy=e.clientY-g.startY,dt=Math.max(1,e.timeStamp-g.lastTime),sample=(e.clientX-g.lastX)/dt;g.vx=g.vx*.65+sample*.35;g.lastX=e.clientX;g.lastTime=e.timeStamp;if(!g.dragging&&(Math.abs(dx)>10||Math.abs(dy)>10)){g.moved=true;if(g.armed&&g.button){g.dragging=true;clearTimeout(g.timer);const r=g.button.getBoundingClientRect();g.offsetX=g.startX-r.left;g.offsetY=g.startY-r.top;g.button.classList.add('bottom-item-dragging');g.ghost=g.button.cloneNode(true);g.ghost.classList.add('bottom-item-drag-ghost');g.ghost.style.width=r.width+'px';g.ghost.style.height=r.height+'px';root.querySelector('.phone').append(g.ghost)}}if(!g.dragging){if(g.moved){clearTimeout(g.timer);nav.scrollLeft=g.startScroll-dx}return}e.preventDefault();const pr=root.querySelector('.phone').getBoundingClientRect();g.ghost.style.left=(e.clientX-pr.left-g.offsetX)+'px';g.ghost.style.top=(e.clientY-pr.top-g.offsetY)+'px';const target=document.elementFromPoint(e.clientX,e.clientY)?.closest?.('.bottom-item');if(!target||target===g.button||target.parentElement!==nav){g.target?.classList.remove('bottom-item-drop-target');g.target=null;return}const r=target.getBoundingClientRect(),before=e.clientX<r.left+r.width/2;g.target?.classList.remove('bottom-item-drop-target');g.target=target;target.classList.add('bottom-item-drop-target');nav.insertBefore(g.button,before?target:target.nextSibling)},{passive:false});
let suppressPointerClickUntil=0;
function finishNav(e){const g=navGesture;if(!g||g.pid!==e.pointerId)return;clearTimeout(g.timer);if(g.dragging){g.button.classList.remove('bottom-item-dragging');g.ghost?.remove();g.target?.classList.remove('bottom-item-drop-target')}if(g.moved&&!g.dragging)snapNav(g.vx);const tapped=e.type==='pointerup'&&!g.moved&&!g.dragging?g.button:null;navGesture=null;suppressPointerClickUntil=performance.now()+700;if(tapped)activatePage(tapped)}
nav.addEventListener('pointerup',finishNav);nav.addEventListener('pointercancel',finishNav);
function activatePage(b){if(!b)return;finishDocEdits();closeSwipe();closeCreate();state.orderMode=false;clearGhost();drag=null;state.page=b.dataset.page;nav.querySelectorAll('.bottom-item').forEach(x=>x.classList.toggle('active',x===b));docView.hidden=true;state.currentDocId='';state.bodyEditing=false;state.titleEditing=false;if(state.page==='knowledge'){knowledge.hidden=false;placeholder.hidden=true;title.textContent='База знаний';if(state.query)showResults(state.query);else renderTree()}else{knowledge.hidden=true;placeholder.hidden=false;const labels={planning:'Планирование',tasks:'Задачи',testing:'Тестирование'};title.textContent=labels[state.page]||'Раздел';placeholder.textContent=state.page==='testing'?'Раздел для тестирования — пока не реализован.':state.page==='planning'?'Планирование — экран пока не прорабатываем.':'Задачи — экран пока не прорабатываем.'}document.dispatchEvent(new CustomEvent('personalos:pagechange',{detail:{page:state.page}}))}
nav.addEventListener('click',e=>{if(e.detail>0&&performance.now()<suppressPointerClickUntil){e.preventDefault();return}activatePage(e.target.closest('.bottom-item'))});
renderTree();
})();
