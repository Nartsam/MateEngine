// MateMmdPhysics：给 Unity 用的 MMD 式刚体物理，Bullet 3.25 外面包一层最小的 C 接口。
//
// 刚体与关节的构造方式与 MMD 系播放器（three.js MMDPhysics、Saba）一致：模式 0 是运动学刚体、
// 关节是 btGeneric6DofSpringConstraint、碰撞按 1<<分组 与掩码过滤。坐标、单位与参数含义的换算
// 全部在 C# 端（Assets/PmxPhysicsRuntime/MmdPhysics.cs）完成；这里除原样转发外，只自己推进固定步长
// 并在子步之间插值（见 mp_step）。
//
// 变换数组统一为每个刚体 7 个 float：px py pz qx qy qz qw。
#include <btBulletDynamicsCommon.h>

#include <memory>
#include <vector>

#define MP_API extern "C" __declspec(dllexport)

namespace
{
struct World
{
    // 成员按依赖顺序声明，构造与析构顺序随之确定。
    btDefaultCollisionConfiguration config;
    btCollisionDispatcher dispatcher;
    btDbvtBroadphase broadphase;
    btSequentialImpulseConstraintSolver solver;
    btDiscreteDynamicsWorld world;
    std::vector<std::unique_ptr<btCollisionShape>> shapes;
    std::vector<std::unique_ptr<btMotionState>> states;
    std::vector<std::unique_ptr<btRigidBody>> bodies;
    std::vector<std::unique_ptr<btTypedConstraint>> joints;
    // 子步由这里自己推进（见 mp_step）：previous 是每个刚体上一个子步开始前的姿态，输出在它与当前姿态之间插值；
    // kinematicFrom/To 是运动学刚体上一帧与本帧的目标姿态，每个子步按它所在的时刻在两者之间插值；
    // lastTarget 记着每个刚体上一帧的目标，作为下一帧的起点。
    std::vector<btTransform> previous, lastTarget;
    // 每个动态刚体输出时参照的运动学刚体（-1 为不参照），见 mp_get_transforms。
    std::vector<int> anchor;
    std::vector<int> kinematicIds;
    std::vector<btTransform> kinematicFrom, kinematicTo;
    btScalar accumulator = 0;
    btScalar fixedTimeStep = btScalar(1) / 60;

    World() : dispatcher(&config), world(&dispatcher, &broadphase, &solver, &config) {}

    ~World()
    {
        for (auto it = joints.rbegin(); it != joints.rend(); ++it) world.removeConstraint(it->get());
        for (auto it = bodies.rbegin(); it != bodies.rend(); ++it) world.removeRigidBody(it->get());
    }

    bool Valid(int id) const { return id >= 0 && id < static_cast<int>(bodies.size()); }
};

btTransform ToTransform(const float* xf)
{
    return btTransform(btQuaternion(xf[3], xf[4], xf[5], xf[6]), btVector3(xf[0], xf[1], xf[2]));
}

void FromTransform(const btTransform& t, float* xf)
{
    const btVector3& p = t.getOrigin();
    const btQuaternion q = t.getRotation();
    xf[0] = p.x(); xf[1] = p.y(); xf[2] = p.z();
    xf[3] = q.x(); xf[4] = q.y(); xf[5] = q.z(); xf[6] = q.w();
}

btVector3 Vec(const float* v) { return btVector3(v[0], v[1], v[2]); }
}

MP_API void* mp_world_create(float gravityY)
{
    auto* w = new World();
    w->world.setGravity(btVector3(0, gravityY, 0));
    return w;
}

MP_API void mp_world_destroy(void* world)
{
    delete static_cast<World*>(world);
}

// 约束求解的迭代次数，Bullet 默认 10。衣物被身体顶住、关节限位又不让它转开时，迭代不足会让两者
// 每步互相推来推去，静止姿势下也在抖。
MP_API void mp_world_set_iterations(void* world, int iterations)
{
    if (iterations > 0) static_cast<World*>(world)->world.getSolverInfo().m_numIterations = iterations;
}

// shape：0 球（size.x 半径）、1 盒（size 为半边长）、2 胶囊（size.x 半径、size.y 圆柱段高度，沿局部 Y）。
// 运动学刚体质量按 0 处理；动态刚体质量为 0 时 Bullet 会把它当静态体，与按原样搬运 PMX 的播放器一致。
MP_API int mp_body_add(void* world, int shape, const float* size, const float* xf, float mass,
                       float linearDamping, float angularDamping, float restitution, float friction,
                       int group, int mask, int kinematic, int additionalDamping)
{
    auto* w = static_cast<World*>(world);
    std::unique_ptr<btCollisionShape> s;
    switch (shape)
    {
        case 0: s = std::make_unique<btSphereShape>(size[0]); break;
        case 1: s = std::make_unique<btBoxShape>(Vec(size)); break;
        default: s = std::make_unique<btCapsuleShape>(size[0], size[1]); break;
    }
    btScalar m = kinematic ? btScalar(0) : btScalar(mass);
    btVector3 inertia(0, 0, 0);
    if (m != 0) s->calculateLocalInertia(m, inertia);

    auto state = std::make_unique<btDefaultMotionState>(ToTransform(xf));
    btRigidBody::btRigidBodyConstructionInfo info(m, state.get(), s.get(), inertia);
    info.m_linearDamping = linearDamping;
    info.m_angularDamping = angularDamping;
    info.m_restitution = restitution;
    info.m_friction = friction;
    info.m_additionalDamping = additionalDamping != 0;
    auto body = std::make_unique<btRigidBody>(info);
    if (kinematic) body->setCollisionFlags(body->getCollisionFlags() | btCollisionObject::CF_KINEMATIC_OBJECT);
    // 桌宠长时间静止，休眠后再被带动会先僵住一下。
    body->setActivationState(DISABLE_DEACTIVATION);
    w->world.addRigidBody(body.get(), 1 << group, mask);

    w->previous.push_back(body->getWorldTransform());
    w->lastTarget.push_back(body->getWorldTransform());
    w->anchor.push_back(-1);
    w->shapes.push_back(std::move(s));
    w->states.push_back(std::move(state));
    w->bodies.push_back(std::move(body));
    return static_cast<int>(w->bodies.size()) - 1;
}

// 关节坐标系 xf 与两个刚体都取绑定姿态，必须在刚体刚创建、尚未移动时调用。
// 限位下限大于上限表示该轴自由、相等表示锁死，与 PMX 的约定相同；弹簧系数为 0 的轴不启用弹簧。
MP_API int mp_joint_add(void* world, int a, int b, const float* xf,
                        const float* linearLower, const float* linearUpper,
                        const float* angularLower, const float* angularUpper,
                        const float* linearSpring, const float* angularSpring,
                        float stopErp, int disableLinkedCollision)
{
    auto* w = static_cast<World*>(world);
    if (!w->Valid(a) || !w->Valid(b) || a == b) return -1;
    btRigidBody& bodyA = *w->bodies[a];
    btRigidBody& bodyB = *w->bodies[b];
    const btTransform jointWorld = ToTransform(xf);
    const btTransform frameA = bodyA.getWorldTransform().inverse() * jointWorld;
    const btTransform frameB = bodyB.getWorldTransform().inverse() * jointWorld;

    auto c = std::make_unique<btGeneric6DofSpringConstraint>(bodyA, bodyB, frameA, frameB, true);
    c->setLinearLowerLimit(Vec(linearLower));
    c->setLinearUpperLimit(Vec(linearUpper));
    c->setAngularLowerLimit(Vec(angularLower));
    c->setAngularUpperLimit(Vec(angularUpper));
    for (int i = 0; i < 3; ++i)
    {
        if (linearSpring[i] != 0)
        {
            c->enableSpring(i, true);
            c->setStiffness(i, linearSpring[i]);
        }
        if (angularSpring[i] != 0)
        {
            c->enableSpring(i + 3, true);
            c->setStiffness(i + 3, angularSpring[i]);
        }
    }
    if (stopErp > 0)
        for (int i = 0; i < 6; ++i) c->setParam(BT_CONSTRAINT_STOP_ERP, stopErp, i);
    w->world.addConstraint(c.get(), disableLinkedCollision != 0);

    w->joints.push_back(std::move(c));
    return static_cast<int>(w->joints.size()) - 1;
}

// 运动学刚体本帧的目标姿态。下一次 mp_step 让每个子步按它所在的时刻，在上一帧与本帧的目标之间插值：
// Bullet 自带的做法是第一个子步就跳到目标，一帧有多个子步时贴着它的衣物会被猛推一下；只按子步序号平分
// 也不行，那样每帧最后一个子步总落在本帧目标上，而它代表的时刻比本帧早 0～1 个子步、每帧不同，渲染帧率
// 高于物理频率时，挂在头上的刘海相对头部忽前忽后地颤。
MP_API void mp_set_kinematic(void* world, int count, const int* ids, const float* xf)
{
    auto* w = static_cast<World*>(world);
    w->kinematicIds.assign(ids, ids + count);
    w->kinematicFrom.resize(count);
    w->kinematicTo.resize(count);
    for (int i = 0; i < count; ++i)
    {
        if (!w->Valid(ids[i])) continue;
        w->kinematicFrom[i] = w->lastTarget[ids[i]];
        w->kinematicTo[i] = ToTransform(xf + 7 * i);
        w->lastTarget[ids[i]] = w->kinematicTo[i];
    }
}

// 动态刚体输出时参照的运动学刚体，一般是它所在的链经由关节挂着的那个（刘海挂在头上、胸部挂在胸骨上）。
MP_API void mp_set_anchors(void* world, int count, const int* ids, const int* anchors)
{
    auto* w = static_cast<World*>(world);
    for (int i = 0; i < count; ++i)
        if (w->Valid(ids[i])) w->anchor[ids[i]] = w->Valid(anchors[i]) ? anchors[i] : -1;
}

// 把刚体直接放到给定姿态并清零速度，用于初始化与瞬移后的重置。
MP_API void mp_reset(void* world, int count, const int* ids, const float* xf)
{
    auto* w = static_cast<World*>(world);
    const btVector3 zero(0, 0, 0);
    for (int i = 0; i < count; ++i)
    {
        if (!w->Valid(ids[i])) continue;
        btRigidBody& body = *w->bodies[ids[i]];
        const btTransform t = ToTransform(xf + 7 * i);
        body.setWorldTransform(t);
        body.setInterpolationWorldTransform(t);
        body.getMotionState()->setWorldTransform(t);
        body.setLinearVelocity(zero);
        body.setAngularVelocity(zero);
        body.setInterpolationLinearVelocity(zero);
        body.setInterpolationAngularVelocity(zero);
        body.clearForces();
        w->previous[ids[i]] = t;
        w->lastTarget[ids[i]] = t;
    }
    w->kinematicIds.clear();
    w->accumulator = 0;
    w->world.updateAabbs();
}

// 固定步长推进，返回本次走了几个子步；追不上的时间直接丢掉，避免越积越多。
// 不用 stepSimulation 自带的固定步长模式：它输出的姿态是拿最后一个子步的速度往回外推的，衣物被身体
// 顶住、与关节限位互相推挤时速度很大而位置几乎不动，外推出来的姿态逐帧乱跳（静止坐姿下也抖）。
// 这里每次只让它走一个子步，输出改为前后两个子步结果之间的线性插值。
MP_API int mp_step(void* world, float dt, int maxSubSteps, float fixedTimeStep)
{
    auto* w = static_cast<World*>(world);
    if (fixedTimeStep <= 0) return 0;
    w->fixedTimeStep = fixedTimeStep;
    // 上一帧末尾已累积的时间：本帧第 k 个子步落在上一帧时刻之后 (fixedTimeStep - before) + k·fixedTimeStep 处。
    const btScalar before = w->accumulator;
    w->accumulator += dt;
    int steps = static_cast<int>(w->accumulator / fixedTimeStep);
    bool dropped = false;
    if (steps > maxSubSteps)
    {
        steps = maxSubSteps;
        w->accumulator = steps * fixedTimeStep;
        dropped = true;
    }
    for (int k = 0; k < steps; ++k)
    {
        // 追不上而丢弃时间时，子步与帧时刻已对不上，退回按序号平分。
        const btScalar f = dropped || dt <= 0 ? btScalar(k + 1) / steps
                                              : btMin((fixedTimeStep - before + k * fixedTimeStep) / dt, btScalar(1));
        for (size_t i = 0; i < w->kinematicIds.size(); ++i)
        {
            if (!w->Valid(w->kinematicIds[i])) continue;
            const btTransform& from = w->kinematicFrom[i];
            const btTransform& to = w->kinematicTo[i];
            const btTransform t(from.getRotation().slerp(to.getRotation(), f), from.getOrigin().lerp(to.getOrigin(), f));
            w->bodies[w->kinematicIds[i]]->getMotionState()->setWorldTransform(t);
        }
        for (size_t i = 0; i < w->bodies.size(); ++i) w->previous[i] = w->bodies[i]->getWorldTransform();
        // maxSubSteps 取 0 是 Bullet 的可变步长模式：只走一步，运动状态与刚体姿态一致，不做外推。
        w->world.stepSimulation(fixedTimeStep, 0, fixedTimeStep);
    }
    w->accumulator -= steps * fixedTimeStep;
    return steps;
}

// 刚体在本帧时刻的姿态。动态刚体取前后两个子步之间按剩余时间插值，得到的是比本帧早一个子步时的姿态；再按它的
// 参照刚体从那时到本帧的整体运动补上（参照刚体同样插值到早一个子步时，与本帧目标相比），只有相对参照刚体的
// 晃动晚一个子步。不补时整个刚体晚一个子步：头一快速转动，贴着限位的刘海就落后头部「角速度 × 子步时长」，
// 转向一变就像在来回甩；胸部也拖在胸骨后面。MMD 按帧时刻推进物理，没有这个延迟。运动学刚体取它当前的姿态。
MP_API void mp_get_transforms(void* world, int count, const int* ids, float* xf)
{
    auto* w = static_cast<World*>(world);
    const btScalar alpha = btMin(w->accumulator / w->fixedTimeStep, btScalar(1));
    auto Lerp = [alpha](const btTransform& p, const btTransform& t)
    { return btTransform(p.getRotation().slerp(t.getRotation(), alpha), p.getOrigin().lerp(t.getOrigin(), alpha)); };
    for (int i = 0; i < count; ++i)
    {
        if (!w->Valid(ids[i])) continue;
        const btRigidBody& body = *w->bodies[ids[i]];
        btTransform t = body.getWorldTransform();
        if (!body.isStaticOrKinematicObject())
        {
            t = Lerp(w->previous[ids[i]], t);
            const int a = w->anchor[ids[i]];
            if (a >= 0 && w->bodies[a]->isKinematicObject())
                t = w->lastTarget[a] * Lerp(w->previous[a], w->bodies[a]->getWorldTransform()).inverse() * t;
        }
        FromTransform(t, xf + 7 * i);
    }
}
