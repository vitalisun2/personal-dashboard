/* Shared in-memory mock data for Planning and Tasks. */
(()=>{
  if(window.personalOS)return;
  const projects=[
    {id:'project-lch',title:'Lost Cyber Hamster',description:'Игровые миры, механики и первый релиз.',milestones:[
      {id:'milestone-engine',title:'Основа уровней',description:'Надёжный движок для длинных игровых сцен.',features:[
        {id:'feature-chunks',title:'Сегменты уровня',description:'Генерация и переходы между секциями.',status:'done'},
        {id:'feature-spawn',title:'Spawn Engine',description:'Спауны препятствий и предметов на длинных уровнях.',status:'done'},
        {id:'feature-cache',title:'Кэш ресурсов',description:'Повторное использование фонов между сегментами.',status:'done'}
      ]},
      {id:'milestone-worlds',title:'Визуальные миры',description:'Первые локации, атмосфера и интерфейс переходов.',features:[
        {id:'feature-paris',title:'Мир Парижа',description:'Фоны, декор и читаемый маршрут уровня.',status:'done'},
        {id:'feature-newyork',title:'Мир Нью-Йорка',description:'Городские фоны и экран выбора мира.',status:'active'},
        {id:'feature-decor',title:'Живое окружение',description:'Вывески, фонари и детали улиц.',status:'planned'}
      ]},
      {id:'milestone-release',title:'Первый релиз',description:'Подготовка сборки и выход к первым игрокам.',features:[
        {id:'feature-balance',title:'Баланс и обучение',description:'Первый игровой опыт и настройка сложности.',status:'planned'},
        {id:'feature-softlaunch',title:'Soft Launch',description:'Чеклист выпуска и обратная связь.',status:'planned'}
      ]}
    ]},
    {id:'project-pos',title:'Personal OS',description:'Единое пространство для знаний, планов и ежедневных дел.',milestones:[
      {id:'milestone-foundation',title:'Основа приложения',description:'Общий интерфейс и офлайн данные.',features:[
        {id:'feature-knowledge',title:'База знаний',description:'Дерево документов и быстрый поиск.',status:'done'},
        {id:'feature-planning',title:'Планирование',description:'Проекты, вехи и фичи.',status:'active'},
        {id:'feature-sync',title:'Синхронизация',description:'Изменения без сети и обмен с сервером.',status:'planned'}
      ]},
      {id:'milestone-agent',title:'Умный помощник',description:'Общий чат и действия через инструменты.',features:[
        {id:'feature-chat',title:'Глобальный чат',description:'История разговоров в любой части приложения.',status:'planned'},
        {id:'feature-tools',title:'Действия агента',description:'Предложение, подтверждение и выполнение действий.',status:'planned'}
      ]}
    ]},
    {id:'project-studio',title:'Творческая студия',description:'Пространство для экспериментов и новых идей.',milestones:[
      {id:'milestone-ideas',title:'Первые концепты',description:'Собрать и оценить направления.',features:[
        {id:'feature-references',title:'Библиотека референсов',description:'Визуальные ориентиры для будущих проектов.',status:'planned'},
        {id:'feature-prototypes',title:'Быстрые прототипы',description:'Проверить несколько идей на практике.',status:'planned'}
      ]}
    ]}
  ];
  const tasks=[
    {id:'task-1',title:'Подготовить фоны Нью-Йорка',projectId:'project-lch',milestoneId:'milestone-worlds',featureId:'feature-newyork',status:'planned',completed:false},
    {id:'task-2',title:'Сверстать экран выбора мира',projectId:'project-lch',milestoneId:'milestone-worlds',featureId:'feature-newyork',status:'backlog',completed:false},
    {id:'task-3',title:'Проверить переходы между сегментами',projectId:'project-lch',milestoneId:'milestone-worlds',featureId:'feature-paris',status:'today',completed:false},
    {id:'task-4',title:'Собрать чеклист релиза',projectId:'project-lch',milestoneId:'milestone-release',featureId:'feature-softlaunch',status:'planned',completed:false},
    {id:'task-5',title:'Описать модель вех и фич',projectId:'project-pos',milestoneId:'milestone-foundation',featureId:'feature-planning',status:'backlog',completed:false},
    {id:'task-6',title:'Прототип карточки проекта',projectId:'project-pos',milestoneId:'milestone-foundation',featureId:'feature-planning',status:'planned',completed:false},
    {id:'task-7',title:'Оплатить хостинг',section:'Личное',description:'Продлить сервер для личных проектов.',status:'today',workStatus:'active',completed:false},
    {id:'task-8',title:'Разобрать заметки недели',section:'Личное',status:'backlog',completed:false},
    {id:'task-9',title:'Подготовить референсы интерфейса',section:'Идеи',status:'backlog',completed:false},
    {id:'task-10',title:'Ответить на письмо',section:'Личное',status:'today',workStatus:'done',completed:true}
  ];
  const listeners=new Set();
  let sequence=100;
  const api={
    projects,tasks,
    nextId(prefix){return `${prefix}-${++sequence}`},
    getProject(id){return projects.find(project=>project.id===id)||null},
    getFeature(id){for(const project of projects)for(const milestone of project.milestones)for(const feature of milestone.features)if(feature.id===id)return feature;return null},
    getTask(id){return tasks.find(task=>task.id===id)||null},
    subscribe(listener){listeners.add(listener);return()=>listeners.delete(listener)},
    notify(){for(const listener of listeners)listener()},
    moveTaskToBacklog(id){const task=api.getTask(id);if(!task||task.status!=='planned')return false;task.status='backlog';api.notify();return true},
    moveTaskToPlan(id){const task=api.getTask(id);if(!task||task.status!=='backlog'||!task.projectId||!task.featureId)return false;task.status='planned';task.completed=false;delete task.workStatus;api.notify();return true}
  };
  window.personalOS=api;
})();
